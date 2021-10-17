using System;
using System.Threading;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;
using Windows.UI.Composition;
using Windows.System.Threading;
using SharpDX;
using SharpDX.Direct3D11;

namespace VirtualMonitor_S
{
    public class CaptureEncode
    {
        //截取区域
        public int LocationX=0;
        public int LocationY=0;
        public bool IsHardwareAcc=true;
        //截取屏幕
        private int _changex, _changey;
        private IDirect3DDevice _device;
        private SharpDX.Direct3D11.Device _d3dDevice;
        private SharpDX.DXGI.SwapChain1 _swapChain;
        private SharpDX.Direct3D11.Multithread _multithread;
        private SharpDX.Direct3D11.Texture2D _composeTexture;//源图
        private SharpDX.Direct3D11.Texture2D _blankComposeTexture;//填补空白
        private SharpDX.Direct3D11.Texture2D _tarComposeTexture;

        
        private SharpDX.Direct3D11.RenderTargetView _composeRenderTargetView;
        private float _oripitch = 0, _oriroll = 0, _oriyaw = 0;

        private ManualResetEvent[] _events;
        private ManualResetEvent _frameEvent;
        private ManualResetEvent _closedEvent;
        private Direct3D11CaptureFrame _currentFrame;

        private GraphicsCaptureItem _captureItem;
        private GraphicsCaptureSession _session;
        private Direct3D11CaptureFramePool _framePool;
        private SizeInt32 _lastSize;
        //帧编码
        private VideoStreamDescriptor _videoDescriptor;
        private MediaStreamSource _mediaStreamSource;
        private MediaTranscoder _transcoder;

        private bool _isRecording;
        private bool _closed = false;

        public CaptureEncode(IDirect3DDevice device, GraphicsCaptureItem item)
        {
            _device = device; _lastSize = item.Size;
            _d3dDevice = Direct3D11Helpers.CreateSharpDXDevice(device);
            _captureItem = item;
            _isRecording = false;
            //_multithread = _d3dDevice.QueryInterface<SharpDX.Direct3D11.Multithread>();
            //_multithread.SetMultithreadProtected(true);
            //_frameEvent = new ManualResetEvent(false);
            //_closedEvent = new ManualResetEvent(false);
            //_events = new[] { _closedEvent, _frameEvent };

            CreateMediaObjects();
        }


        private void CreateMediaObjects()
        {
            var videoProperties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, 1920, 1080);
            _videoDescriptor = new VideoStreamDescriptor(videoProperties);

            _mediaStreamSource = new MediaStreamSource(_videoDescriptor);
            _mediaStreamSource.BufferTime = TimeSpan.FromSeconds(0);
            _mediaStreamSource.Starting += OnMediaStreamSourceStarting;
            _mediaStreamSource.SampleRequested += OnMediaStreamSourceSampleRequested;

            _transcoder = new MediaTranscoder();
            _transcoder.HardwareAccelerationEnabled = IsHardwareAcc;
        }

        public async Task EncodeAsync(IRandomAccessStream stream, uint width, uint height, uint bitrateInBps, uint frameRate)
        {
            if (!_isRecording)
            {
                _isRecording = true;

                _multithread = _d3dDevice.QueryInterface<SharpDX.Direct3D11.Multithread>();
                _multithread.SetMultithreadProtected(true);
                _frameEvent = new ManualResetEvent(false);
                _closedEvent = new ManualResetEvent(false);
                _events = new[] { _closedEvent, _frameEvent };
                
                InitializeComposeTexture(_captureItem.Size);
                InitializeCapture(_captureItem.Size);

                var encodingProfile = new MediaEncodingProfile();
                encodingProfile.Container.Subtype = "MPEG4";
                encodingProfile.Video.Subtype = "H264";
                encodingProfile.Video.Width = 1920;
                encodingProfile.Video.Height = 1080;
                encodingProfile.Video.Bitrate = bitrateInBps;
                encodingProfile.Video.FrameRate.Numerator = frameRate;
                encodingProfile.Video.FrameRate.Denominator = 1;
                encodingProfile.Video.PixelAspectRatio.Numerator = 1;
                encodingProfile.Video.PixelAspectRatio.Denominator = 1;
                var transcode = await _transcoder.PrepareMediaStreamSourceTranscodeAsync(_mediaStreamSource, stream, encodingProfile);

                try
                {
                    await transcode.TranscodeAsync();
                }
                catch (Exception ex)
                {
                    Debugger.Break();
                }
            }
        }


        private void InitializeComposeTexture(SizeInt32 size)
        {
            var description = new SharpDX.Direct3D11.Texture2DDescription {
                Width = size.Width,
                Height = size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SharpDX.DXGI.SampleDescription() {
                    Count = 1,
                    Quality = 0
                },
                Usage = SharpDX.Direct3D11.ResourceUsage.Default,
                BindFlags = SharpDX.Direct3D11.BindFlags.ShaderResource | SharpDX.Direct3D11.BindFlags.RenderTarget,
                CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.None,
                OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None
            };
            var tardescription = new SharpDX.Direct3D11.Texture2DDescription {
                Width = 1920,
                Height = 1080,
                MipLevels = 1,
                ArraySize = 1,
                Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SharpDX.DXGI.SampleDescription() {
                    Count = 1,
                    Quality = 0
                },
                Usage = SharpDX.Direct3D11.ResourceUsage.Default,
                BindFlags = SharpDX.Direct3D11.BindFlags.ShaderResource | SharpDX.Direct3D11.BindFlags.RenderTarget,
                CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.None,
                OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None
            };
            _tarComposeTexture = new SharpDX.Direct3D11.Texture2D(_d3dDevice, tardescription);
            
            _composeTexture = new SharpDX.Direct3D11.Texture2D(_d3dDevice, description);
            _composeRenderTargetView = new SharpDX.Direct3D11.RenderTargetView(_d3dDevice, _composeTexture);

            byte[] data = new byte[1920 * 1080 * 4];
            for (int i = 0; i < 1920 * 1080 * 4; i++) if ((i + 1) % 4 == 0) data[i] = 255; else data[i] = 255;
            DataStream s = DataStream.Create(data, true, true);
            DataRectangle rect = new DataRectangle(s.DataPointer, 1920 * 4);
            _blankComposeTexture = new SharpDX.Direct3D11.Texture2D(_d3dDevice, tardescription, rect);
        }

        private void InitializeCapture(SizeInt32 size)
        {
            //var dxgiDevice = _d3dDevice.QueryInterface<SharpDX.DXGI.Device>();
            //var adapter = dxgiDevice.GetParent<SharpDX.DXGI.Adapter>();
            //var factory = adapter.GetParent<SharpDX.DXGI.Factory2>();

            //var description = new SharpDX.DXGI.SwapChainDescription1 {
            //    Width = 1,
            //    Height =1,
            //    Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
            //    Usage = SharpDX.DXGI.Usage.RenderTargetOutput,
            //    SampleDescription = new SharpDX.DXGI.SampleDescription() {
            //        Count = 1,
            //        Quality = 0
            //    },
            //    BufferCount = 2,
            //    Scaling = SharpDX.DXGI.Scaling.Stretch,
            //    SwapEffect = SharpDX.DXGI.SwapEffect.FlipSequential,
            //    AlphaMode = SharpDX.DXGI.AlphaMode.Premultiplied
            //};
            //var swapChain = new SharpDX.DXGI.SwapChain1(factory, dxgiDevice, ref description);

            //using (var backBuffer = swapChain.GetBackBuffer<SharpDX.Direct3D11.Texture2D>(0))
            //using (var renderTargetView = new SharpDX.Direct3D11.RenderTargetView(_d3dDevice, backBuffer))
            //{
            //    _d3dDevice.ImmediateContext.ClearRenderTargetView(renderTargetView, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0));
            //}
            //_swapChain = swapChain;

            _captureItem.Closed += OnClosed;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                   _device,
                   DirectXPixelFormat.B8G8R8A8UIntNormalized,
                   2,
                   size);
            _framePool.FrameArrived += OnFrameArrived;
            _session = _framePool.CreateCaptureSession(_captureItem);
            _session.StartCapture();
        }


        private void OnMediaStreamSourceStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            using (var frame = WaitForNewFrame())
            {
                args.Request.SetActualStartPosition(frame.SystemRelativeTime);
            }
        }

        private void OnMediaStreamSourceSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            if (_isRecording && !_closed)
            {
                try
                {
                    using (var frame = WaitForNewFrame())
                    {
                        if (frame == null)
                        {
                            args.Request.Sample = null;
                            Dispose();
                            return;
                        }

                        var timeStamp = frame.SystemRelativeTime;
                        var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface, timeStamp);

                        args.Request.Sample = sample;
                    }
                }
                catch (Exception e)
                {
                    System.Diagnostics.Debugger.Break();
                    Debug.WriteLine(e.Message);
                    Debug.WriteLine(e.StackTrace);
                    Debug.WriteLine(e);
                    args.Request.Sample = null;
                    Dispose();
                }
            }
            else
            {
                args.Request.Sample = null;
                Dispose();
            }
        }

        private void OnClosed(GraphicsCaptureItem sender, object args)
        {
            _closedEvent.Set();
        }
        
        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            #region 帧池动态重构 暂存
            //var newSize = false;

            //using (var frame = sender.TryGetNextFrame())
            //{
            //    if (frame.ContentSize.Width != _lastSize.Width ||
            //        frame.ContentSize.Height != _lastSize.Height)
            //    {
            //        // 源已改变,故需要变换抓取大小,首先改变swap chain,之后是Texture
            //        newSize = true;
            //        _lastSize = frame.ContentSize;
            //        _swapChain.ResizeBuffers(
            //            2,
            //            _lastSize.Width,
            //            _lastSize.Height,
            //            SharpDX.DXGI.Format.B8G8R8A8_UNorm,
            //            SharpDX.DXGI.SwapChainFlags.None);
            //    }

            //    using (var sourceTexture = Direct3D11Helpers.CreateSharpDXTexture2D(frame.Surface))
            //    using (var backBuffer = _swapChain.GetBackBuffer<SharpDX.Direct3D11.Texture2D>(0))
            //    using (var renderTargetView = new SharpDX.Direct3D11.RenderTargetView(_d3dDevice, backBuffer))
            //    {
            //        _d3dDevice.ImmediateContext.ClearRenderTargetView(renderTargetView, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 1));
            //        _d3dDevice.ImmediateContext.CopyResource(sourceTexture, backBuffer);
            //    }

            //}

            //_swapChain.Present(1, SharpDX.DXGI.PresentFlags.None);

            //if (newSize)//帧池重构
            //{
            //    _framePool.Recreate(
            //        _device,
            //        DirectXPixelFormat.B8G8R8A8UIntNormalized,
            //        2,
            //        _lastSize);
            //}
            #endregion

            _currentFrame = sender.TryGetNextFrame();
            _frameEvent.Set();
        }


        private void Cleanup()
        {
            _framePool?.Dispose();
            _session?.Dispose();
            if (_captureItem != null)
            {
                _captureItem.Closed -= OnClosed;
            }
            _captureItem = null;
            _device = null;
            _d3dDevice = null;
            _composeTexture?.Dispose();
            _composeTexture = null;
            _composeRenderTargetView?.Dispose();
            _composeRenderTargetView = null;
            _currentFrame?.Dispose();
        }

        private SurfaceWithInfo WaitForNewFrame()
        {
            _currentFrame?.Dispose();
            _frameEvent.Reset();//获取新帧
            var whiteRegion = new SharpDX.Direct3D11.ResourceRegion(0, 0, 0, 1920, 1080, 1);

            var signaledEvent = _events[WaitHandle.WaitAny(_events)];
            if (signaledEvent == _closedEvent)
            {
                Cleanup();
                return null;
            }

            var result = new SurfaceWithInfo { SystemRelativeTime = _currentFrame.SystemRelativeTime };
            using (var multithreadLock = new MultithreadLock(_multithread))
            using (var sourceTexture = Direct3D11Helpers.CreateSharpDXTexture2D(_currentFrame.Surface))
            {
                _d3dDevice.ImmediateContext.ClearRenderTargetView(_composeRenderTargetView, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 1));

                var region = new SharpDX.Direct3D11.ResourceRegion(LocationX>0?LocationX:0, LocationY>0?LocationY:0, 0,
                    (LocationX + 1920 > sourceTexture.Description.Width ? sourceTexture.Description.Width : LocationX + 1920),
                    (LocationY + 1080 > sourceTexture.Description.Height ? sourceTexture.Description.Height : LocationY + 1080), 1);
                _d3dDevice.ImmediateContext.CopySubresourceRegion(sourceTexture, 0, whiteRegion, _blankComposeTexture, 0);//覆盖掉原本区域
                _d3dDevice.ImmediateContext.CopySubresourceRegion(sourceTexture, 0, region, _tarComposeTexture, LocationX>=0?0:(1920+LocationX>0? 1920 + LocationX - 1:0),
                    LocationY>=0?0:(1080+LocationY-1>0? 1080 + LocationY - 1:0), 0);//_targetTexture为剪切后的区域
                //https://stackoverflow.com/questions/64130136/texture2d-from-byte-array-sharpdx
                //实现空白区域
                var description = _tarComposeTexture.Description;
                description.Usage = SharpDX.Direct3D11.ResourceUsage.Default;
                description.BindFlags = SharpDX.Direct3D11.BindFlags.ShaderResource | SharpDX.Direct3D11.BindFlags.RenderTarget;
                description.CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.None;
                description.OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None;
                using (var copyTexture = new SharpDX.Direct3D11.Texture2D(_d3dDevice, description))
                {
                    _d3dDevice.ImmediateContext.CopyResource(_tarComposeTexture, copyTexture);

                    result.Surface = Direct3D11Helpers.CreateDirect3DSurfaceFromSharpDXTexture(copyTexture);
                }
                
            }
            return result;
        }

        public void Dispose()
        {
            _closedEvent.Set();
            _framePool?.Dispose();
            _session?.Dispose();
            if (_captureItem != null)
            {
                _captureItem.Closed -= OnClosed;
            }
            _captureItem = null;
            _device = null;
            _d3dDevice = null;
            _composeTexture?.Dispose();
            _composeTexture = null;
            _composeRenderTargetView?.Dispose();
            _composeRenderTargetView = null;
            _currentFrame?.Dispose();

            //_session?.Dispose();
            //_swapChain?.Dispose();

            //_swapChain = null;
            //_framePool = null;
            //_session = null;
            //_captureItem = null;
        }

        public void ChangeLocation(float pitch,float roll,float yaw)//角度转为x、y位置（临时测试）
        {
            if(_oripitch==0&&_oriroll==0&&_oriyaw==0)
            {
                _oripitch = pitch;_oriroll = roll;_oriyaw = yaw;
            }
            else
            {
                if(((_oriyaw-yaw)>0?_oriyaw:yaw-_oriyaw)>300)
                {
                    _changex = (int)(-(yaw - _oriyaw) * 30);
                    LocationX +=_changex+LocationX>0? _changex + LocationX:0;
                }
                if(((_oripitch-pitch)>0?_oripitch:pitch)>300)
                {
                    _changey = (int)((pitch - _oripitch) * 5);
                    LocationY += _changey+LocationY>0?_changey:0;
                }
            }
        }
      
    }


    class MultithreadLock : IDisposable
    {
        public MultithreadLock(SharpDX.Direct3D11.Multithread multithread)
        {
            _multithread = multithread;
            _multithread?.Enter();
        }

        public void Dispose()
        {
            _multithread?.Leave();
            _multithread = null;
        }

        private SharpDX.Direct3D11.Multithread _multithread;
    }

    public sealed class SurfaceWithInfo : IDisposable
    {
        public IDirect3DSurface Surface { get; internal set; }
        public TimeSpan SystemRelativeTime { get; internal set; }
        public void Dispose()
        {
            Surface?.Dispose();
            Surface = null;
        }
    }


}
