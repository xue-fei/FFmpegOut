using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

namespace FFmpegOut
{
    public sealed class FFmpegSession : System.IDisposable
    {
        #region Factory methods

        public static FFmpegSession Create(
            string name,
            int width, int height, float frameRate,
            FFmpegPreset preset)
        {
            name += System.DateTime.Now.ToString(" yyyy MMdd HHmmss");
            var path = name.Replace(" ", "_") + preset.GetSuffix();
            return CreateWithOutputPath(path, width, height, frameRate, preset);
        }

        public static FFmpegSession CreateWithOutputPath(
            string outputPath,
            int width, int height, float frameRate,
            FFmpegPreset preset)
        {
            return new FFmpegSession(
                "-y -f rawvideo -vcodec rawvideo -pixel_format rgba"
                + " -colorspace bt709"
                + " -video_size " + width + "x" + height
                + " -framerate " + frameRate
                + " -loglevel warning -i - " + preset.GetOptions()
                + " \"" + outputPath + "\"",
                width, height
            );
        }

        public static FFmpegSession CreateWithArguments(string arguments)
        {
            return new FFmpegSession(arguments, 0, 0);
        }

        #endregion

        #region Public members

        public void PushFrame(Texture source)
        {
            if (_pipe != null)
            {
                ProcessQueue();
                if (source != null) QueueFrame(source);
            }
        }

        public void CompletePushFrames()
        {
            _pipe?.SyncFrameData();
        }

        public void Close()
        {
            if (_pipe != null)
            {
                var error = _pipe.CloseAndGetOutput();
                if (!string.IsNullOrEmpty(error))
                    Debug.LogWarning(
                        "FFmpeg returned with warning/error messages. " +
                        "See the following lines for details:\n" + error);

                _pipe.Dispose();
                _pipe = null;
            }

            if (_blitMaterial != null)
            {
                UnityEngine.Object.Destroy(_blitMaterial);
                _blitMaterial = null;
            }

            // 销毁预分配的 RT 池
            foreach (var rt in _rtPool)
            {
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }
            _rtPool.Clear();
        }

        public void Dispose() => Close();

        #endregion

        #region Private objects and constructor

        FFmpegPipe _pipe;
        Material _blitMaterial;
        int _width;
        int _height;

        // 预分配固定尺寸的 RT 池，避免 GetTemporary 被 HDRP 提前回收
        const int PoolSize = 8;
        List<RenderTexture> _rtPool = new List<RenderTexture>(PoolSize);
        int _rtIndex = 0;

        FFmpegSession(string arguments, int width, int height)
        {
            _width = width;
            _height = height;

            if (!FFmpegPipe.IsAvailable)
                Debug.LogWarning(
                    "Failed to initialize an FFmpeg session due to missing " +
                    "executable file. Please check FFmpeg installation.");
            else if (!UnityEngine.SystemInfo.supportsAsyncGPUReadback)
                Debug.LogWarning(
                    "Failed to initialize an FFmpeg session due to lack of " +
                    "async GPU readback support. Please try changing " +
                    "graphics API to readback-enabled one.");
            else
                _pipe = new FFmpegPipe(arguments);
        }

        ~FFmpegSession()
        {
            if (_pipe != null)
                Debug.LogError(
                    "An unfinalized FFmpegSession object was detected. " +
                    "It should be explicitly closed or disposed " +
                    "before being garbage-collected.");
        }

        // 从池里取下一个 RT（轮转），若未创建则新建
        RenderTexture GetPooledRT(int width, int height)
        {
            // 池未满时扩容
            if (_rtPool.Count < PoolSize)
            {
                var newRT = new RenderTexture(width, height, 0,
                    RenderTextureFormat.ARGB32);
                newRT.Create();
                _rtPool.Add(newRT);
                return newRT;
            }

            // 轮转取用
            var rt = _rtPool[_rtIndex];
            _rtIndex = (_rtIndex + 1) % PoolSize;

            // 尺寸变化时重建（正常不应发生，但作为安全保障）
            if (rt.width != width || rt.height != height)
            {
                rt.Release();
                rt.width = width;
                rt.height = height;
                rt.Create();
            }

            return rt;
        }

        #endregion

        #region Frame readback queue

        List<AsyncGPUReadbackRequest> _readbackQueue =
            new List<AsyncGPUReadbackRequest>(4);

        void QueueFrame(Texture source)
        {
            if (_readbackQueue.Count > 6)
            {
                Debug.LogWarning("Too many GPU readback requests.");
                return;
            }

            if (_blitMaterial == null)
            {
                var shader = Shader.Find("Hidden/FFmpegOut/Preprocess");
                if (shader == null)
                {
                    Debug.LogError(
                        "[FFmpegOut] Shader 'Hidden/FFmpegOut/Preprocess' not found!");
                    return;
                }
                _blitMaterial = new Material(shader);
            }

            // 使用固定尺寸（session 创建时决定），忽略 source 的实际尺寸
            // 这样保证每帧发给 FFmpeg 的数据大小完全一致
            int w = _width > 0 ? _width : source.width;
            int h = _height > 0 ? _height : source.height;

            // 从池中取持久 RT，不会被 HDRP 提前回收
            var rt = GetPooledRT(w, h);

            var cmd = CommandBufferPool.Get("FFmpegOut_Preprocess");
            cmd.Blit(source, rt, _blitMaterial, 0);
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            // 直接对持久 RT 发起 readback
            _readbackQueue.Add(AsyncGPUReadback.Request(rt));
        }

        void ProcessQueue()
        {
            while (_readbackQueue.Count > 0)
            {
                if (!_readbackQueue[0].done)
                {
                    if (_readbackQueue.Count > 1 && _readbackQueue[1].done)
                        _readbackQueue[0].WaitForCompletion();
                    else
                        break;
                }

                var req = _readbackQueue[0];
                _readbackQueue.RemoveAt(0);

                if (req.hasError)
                {
                    Debug.LogWarning("GPU readback error was detected.");
                    continue;
                }

                _pipe.PushFrameData(req.GetData<byte>());
            }
        }

        #endregion
    }
}