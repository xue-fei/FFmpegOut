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
                + " -colorspace bt709 -color_trc bt709 -color_primaries bt709"
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

            foreach (var entry in _readbackQueue)
            {
                if (entry.rt != null)
                {
                    entry.rt.Release();
                    UnityEngine.Object.Destroy(entry.rt);
                }
            }
            _readbackQueue.Clear();
        }

        public void Dispose() => Close();

        #endregion

        #region Private objects and constructor

        FFmpegPipe _pipe;
        Material _blitMaterial;
        int _width;
        int _height;

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

        #endregion

        #region Frame readback queue

        struct ReadbackEntry
        {
            public AsyncGPUReadbackRequest request;
            public RenderTexture rt;
        }

        List<ReadbackEntry> _readbackQueue = new List<ReadbackEntry>(4);

        void QueueFrame(Texture source)
        {
            if (_readbackQueue.Count > 6)
            {
                Debug.LogWarning("Too many GPU readback requests.");
                return;
            }

            if (source == null)
            {
                Debug.LogWarning("[FFmpegOut] QueueFrame called with null source.");
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

            int w = _width > 0 ? _width : source.width;
            int h = _height > 0 ? _height : source.height;

            var rt = new RenderTexture(w, h, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            rt.Create();

            var cmd = CommandBufferPool.Get("FFmpegOut_Preprocess");
            cmd.Blit(source, rt, _blitMaterial, 0);
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            _readbackQueue.Add(new ReadbackEntry
            {
                request = AsyncGPUReadback.Request(rt, 0, TextureFormat.ARGB32),
                rt = rt
            });
        }

        void ProcessQueue()
        {
            while (_readbackQueue.Count > 0)
            {
                var entry = _readbackQueue[0];

                if (!entry.request.done)
                {
                    if (_readbackQueue.Count > 1 && _readbackQueue[1].request.done)
                        entry.request.WaitForCompletion();
                    else
                        break;
                }

                _readbackQueue.RemoveAt(0);

                if (entry.request.hasError)
                {
                    Debug.LogWarning("GPU readback error was detected.");
                }
                else
                {
                    var data = entry.request.GetData<byte>();
                    _pipe.PushFrameData(data);
                }

                if (entry.rt != null)
                {
                    entry.rt.Release();
                    UnityEngine.Object.Destroy(entry.rt);
                }
            }
        }

        #endregion
    }
}
