using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using System.Collections;

namespace FFmpegOut
{
    [AddComponentMenu("FFmpegOut/Camera Capture")]
    public sealed class CameraCapture : MonoBehaviour
    {
        #region Public properties
        [SerializeField] int _width = 1920;
        public int width { get { return _width; } set { _width = value; } }

        [SerializeField] int _height = 1080;
        public int height { get { return _height; } set { _height = value; } }

        [SerializeField] FFmpegPreset _preset;
        public FFmpegPreset preset { get { return _preset; } set { _preset = value; } }

        [SerializeField] float _frameRate = 60;
        public float frameRate { get { return _frameRate; } set { _frameRate = value; } }
        #endregion

        #region Private members
        FFmpegSession _session;
        RenderTexture _captureRT;
        RTHandle _captureRTHandle;
        CustomPassVolume _customPassVolume;
        #endregion

        #region Time-keeping
        int _frameCount;
        float _startTime;
        int _frameDropCount;

        float FrameTime => _startTime + (_frameCount - 0.5f) / _frameRate;

        void WarnFrameDrop()
        {
            if (++_frameDropCount != 10) return;
            Debug.LogWarning(
                "Significant frame dropping detected. " +
                "Decrease the recording frame rate.");
        }
        #endregion

        #region MonoBehaviour
        void OnValidate()
        {
            _width = Mathf.Max(8, _width);
            _height = Mathf.Max(8, _height);
        }

        void OnDisable()
        {
            if (_session != null)
            {
                _session.Close();
                _session.Dispose();
                _session = null;
            }

            if (_customPassVolume != null)
            {
                Destroy(_customPassVolume);
                _customPassVolume = null;
            }

            if (_captureRTHandle != null)
            {
                _captureRTHandle.Release();
                _captureRTHandle = null;
            }

            if (_captureRT != null)
            {
                _captureRT.Release();
                Destroy(_captureRT);
                _captureRT = null;
            }
        }

        IEnumerator Start()
        {
            for (var eof = new WaitForEndOfFrame(); ;)
            {
                yield return eof;
                _session?.CompletePushFrames();
            }
        }

        void Update()
        {
            if (_session == null)
            {
                // 1. 创建普通 RenderTexture 供 FFmpeg 读回
                _captureRT = new RenderTexture(_width, _height, 0,
                    RenderTextureFormat.ARGB32);
                _captureRT.Create();

                // 2. 把它包装成 RTHandle，供 HDRP Custom Pass 写入
                _captureRTHandle = RTHandles.Alloc(
                    _captureRT,
                    transferOwnership: false   // 我们自己管理 RT 生命周期
                );

                // 3. 挂载 Custom Pass
                _customPassVolume =
                    gameObject.AddComponent<CustomPassVolume>();
                _customPassVolume.injectionPoint =
                    CustomPassInjectionPoint.AfterPostProcess;
                _customPassVolume.isGlobal = true;

                var pass = new CaptureCustomPass(_captureRTHandle)
                {
                    name = "FFmpegOut Capture",
                    enabled = true
                };
                _customPassVolume.customPasses.Add(pass);

                // 4. 启动 FFmpeg
                _session = FFmpegSession.Create(
                    gameObject.name,
                    _width, _height,
                    _frameRate, _preset
                );

                _startTime = Time.time;
                _frameCount = 0;
                _frameDropCount = 0;
            }

            var gap = Time.time - FrameTime;
            var delta = 1f / _frameRate;

            if (gap < 0)
            {
                _session.PushFrame(null);
            }
            else if (gap < delta)
            {
                _session.PushFrame(_captureRT);
                _frameCount++;
            }
            else if (gap < delta * 2)
            {
                _session.PushFrame(_captureRT);
                _session.PushFrame(_captureRT);
                _frameCount += 2;
            }
            else
            {
                WarnFrameDrop();
                _session.PushFrame(_captureRT);
                _frameCount += Mathf.FloorToInt(gap * _frameRate);
            }
        }
        #endregion
    }

    // ── HDRP Custom Pass ──────────────────────────────────────────────────
    sealed class CaptureCustomPass : CustomPass
    {
        RTHandle _dst;

        public CaptureCustomPass(RTHandle dst)
        {
            _dst = dst;
        }

        protected override void Execute(CustomPassContext ctx)
        {
            if (_dst == null) return;

            // HDUtils.BlitCameraTexture 是 HDRP 官方推荐的
            // RTHandle → RTHandle 拷贝方式，会正确处理格式转换和翻转
            HDUtils.BlitCameraTexture(ctx.cmd, ctx.cameraColorBuffer, _dst);
        }

        protected override void Cleanup()
        {
            // RTHandle 生命周期由 CameraCapture 管理，这里不释放
        }
    }
}