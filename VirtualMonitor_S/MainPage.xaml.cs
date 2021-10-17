using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml.Hosting;
using System.Numerics;
using Windows.UI.Composition;
using Windows.UI.Xaml.Navigation;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Input;
using System.IO;
using Windows.UI.Core;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WatsonWebsocket;
using System.Text;
using Windows.Media.Playback;
using Windows.Media.Core;
using System;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using System;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Media;
using Windows.Media.MediaProperties;
using Microsoft.Samples.SimpleCommunication;
using System.Threading;
using System.Runtime.InteropServices.WindowsRuntime;

namespace VirtualMonitor_S
{
    public sealed partial class MainPage : Page,INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private CapturePreview _preview;
        private IDirect3DDevice _device;
        private CompositionSurfaceBrush _previewBrush;
        private SpriteVisual _previewVisual;


        private CaptureEncode _encoder;
        private IRandomAccessStream _encoderStream;
        private GraphicsCaptureItem _item;
        private uint FrameRate = 75;
        private uint ByteRate = 35000000;

        private string res= "目前无连接的头显";
        private string _response { get { return res; }set { if (value != res) { res = value; NotifyPropertyChanged(); } } }

        private RfcommServiceProvider _rfcommProvider;
        private StreamSocketListener _controllerSocketListener;
        private StreamSocket _socket;
        private DataWriter _writer;

        static string CLIENTPORT = "11110";
        static string SERVERPORT = "11111";


        #region 测试属性
        StorageFile file;
        Stream _stream;
        byte[] buffer;

        DispatcherTimer StopWatch = new DispatcherTimer();//定义计时器,用于双击退出

        #endregion

        private Windows.Networking.Sockets.StreamSocket tcpsocket;
        public MainPage()
        {
            this.InitializeComponent();

            #region 创建预览
            var compositor = Window.Current.Compositor;
            _previewBrush = compositor.CreateSurfaceBrush();
            _previewBrush.Stretch = CompositionStretch.Uniform;
            var shadow = compositor.CreateDropShadow();
            shadow.Mask = _previewBrush;
            _previewVisual = compositor.CreateSpriteVisual();
            _previewVisual.RelativeSizeAdjustment = Vector2.One;
            _previewVisual.Brush = _previewBrush;
            _previewVisual.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(PreviewGrid, _previewVisual);
            #endregion

            _device = D3DDeviceManager.Device;

            tcpsocket = new StreamSocket();

        }


        private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
            }
        }
        private async void HMDConnButton_Click(object sender, RoutedEventArgs e)    
        {
            HMDConnButton.IsEnabled = false;

            //buffer = new byte[ByteRate/FrameRate*60*10];//存储10s需要的大小
            //buffer = new byte[1395 * 1024 * 30];
            //_stream = new MemoryStream(buffer);
            //_stream.Seek(0, SeekOrigin.Begin);

            StopWatch.Interval = TimeSpan.FromSeconds(6);
            StopWatch.Tick += Time_Tick;
            StopWatch.Start();

            //testWrite();

            //if (_item != null)
            //{
            //    _encoder = new CaptureEncode(_device, _preview.Target);
            //    file = await GetTempFileAsync();
            //    try
            //    {
            //        _encoderStream = await file.OpenAsync(FileAccessMode.ReadWrite);
            //        await _encoder.EncodeAsync(_encoderStream, 1920, 1080, ByteRate, FrameRate);
            //        //await _encoder.EncodeAsync(_stream.AsRandomAccessStream(), 1920, 1080, ByteRate, FrameRate);
            //    }
            //    catch (Exception ex)
            //    {
            //        Debugger.Break();
            //    }
            //}
            HMDConnButton.IsEnabled = true;



            ///解析MP4文件
            StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/test.mp4"));
            buffer = new byte[42 * 1024 * 1024];

            //_stream = new MemoryStream(buffer);
            //_stream.Seek(0, SeekOrigin.Begin);

            try
            {
                using (Stream stream = await file.OpenStreamForReadAsync())
                {
                    stream.Read(buffer, 0, (int)stream.Length);
                }
            }
            catch (Exception ex)
            {
                Debugger.Break();
            }
            //_stream.Dispose();
            AnalyseData(buffer);
        }


        #region 测试
        private async void Time_Tick(object sender, object e)
        {
            _encoder?.Dispose();//null检查后释放
            //_stream.Seek(0, SeekOrigin.Begin);
            //int i = 0;


            //var file = await GetTempFileAsync();
            //var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            //await FileIO.WriteBytesAsync(file, buffer);
            //stream.Dispose();
            //MediaPlayer player = new MediaPlayer();
            //PlaybackVideo.SetMediaPlayer(player);
            //_stream.Seek(0, SeekOrigin.Begin);
            //player.SetStreamSource(_stream.AsRandomAccessStream());

            //player.Play();
            StopWatch.Stop();
            //PreviewPlayer.Source = MediaSource.CreateFromStorageFile(file);

            //if (i > 0)
            //    i--;
        }

        private async void testWrite()
        {

            if (_item != null)
            {
                _encoder = new CaptureEncode(_device, _item);
                await _encoder.EncodeAsync(_stream.AsRandomAccessStream(), 1920, 1080, 36000000, 60);
            }
        }
        private async Task<StorageFile> GetTempFileAsync()
        {
            var folder = ApplicationData.Current.TemporaryFolder;
            var name = DateTime.Now.ToString("yyyyMMdd-HHmm-ss");
            var file = await folder.CreateFileAsync($"{name}.mp4");
            return file;
        }

        private void AnalyseData(byte[] data)
        {
            //起始位置,值应该是00
            int i0 = 80, i1, i2, i3;
            int length, count = 0;
            int[] header=new int[14];
            try
            {
                for(int i=0;i0<data.Length;i0++,i++)
                {
                    if (i % 8 == 0) Debug.WriteLine("");
                    Debug.Write(" "+data[i0]);
                }
                while (i0 < data.Length)
                {
                    i1 = i0 + 1; i2 = i0 + 2; i3 = i0 + 3;
                    length = (data[i0] << 24) + (data[i1] << 16) + (data[i2] << 8) + data[i3];

                    if (length == 0) { Debug.WriteLine( "\n" + "遇到0,停止推进,当前NALU个数:" + count.ToString() + 
                        "\nI帧个数：" + header[5].ToString() +"，非I帧： "+(header[1]+header[2]+header[3]+header[4]).ToString()+
                        ", SPS：" + header[7]+", "+", PPS： "+header[8]+"，SEI： "+header[6]+"，其他： "+header[0]); break; }
                    else {
                        Debug.WriteLine(data[i0].ToString() +" "+ data[i1].ToString() + " " + data[i2].ToString() + " " +
                            data[i3].ToString() + "   "+length.ToString() + ",");
                        i0 += length+4; count++;
                        if ((data[i3 + 1] & 0x1F) >= 1 && (data[i3 + 1] & 0x1F) <= 13)
                            header[data[i3 + 1] & 0x1F]++;
                        else
                            header[0]++;
                    }
                }
            }
            catch (Exception ex)
            {
                Debugger.Break();
            }
        }

        #endregion

        private void ControConnButton_Click(object sender, RoutedEventArgs e)
        {
            InitializeControllerServer();
        }
        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new GraphicsCapturePicker();
            var item = await picker.PickSingleItemAsync();
            if (item != null)
            {
                StartPreview(item);
            }
            else
            {
                StopPreview();
            }



            _item = item;

        }


        private void StartPreview(GraphicsCaptureItem item)
        {
            var compositor = Window.Current.Compositor;
            _preview?.Dispose();
            _preview = new CapturePreview(_device, item);
            var surface = _preview.CreateSurface(compositor);
            _previewBrush.Surface = surface;
            _preview.StartCapture();

            StartButton.IsEnabled = false;
        }
        private void StopPreview()
        {
            _preview?.Dispose();
            _preview = null;

            StartButton.IsEnabled = true;
        }

        #region 控制器
        private async void InitializeControllerServer()
        {
            ControConnButton.IsEnabled = false;

            try
            {
                _rfcommProvider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(Constants.RfcommChatServiceUuid));
            }
            // Catch exception HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_AVAILABLE).
            catch (Exception ex) when ((uint)ex.HResult == 0x800710DF)
            {
                Debugger.Break();
                ControConnButton.IsEnabled = true;
                return;
            }


            // Create a listener for this service and start listening
            _controllerSocketListener = new StreamSocketListener();
            _controllerSocketListener.ConnectionReceived += OnControllerDataReceived;
            var rfcomm = _rfcommProvider.ServiceId.AsString();

            await _controllerSocketListener.BindServiceNameAsync(_rfcommProvider.ServiceId.AsString(),
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

            // Set the SDP attributes and start Bluetooth advertising
            InitializeServiceSdpAttributes(_rfcommProvider);

            try
            {
                _rfcommProvider.StartAdvertising(_controllerSocketListener, true);
            }
            catch (Exception e)
            {
                Debugger.Break();
                ControConnButton.IsEnabled = true;
                return;
            }

            ControTextBlock.Text = "连接中……";
        }

        private void InitializeServiceSdpAttributes(RfcommServiceProvider rfcommProvider)
        {
            var sdpWriter = new DataWriter();
            sdpWriter.WriteByte(Constants.SdpServiceNameAttributeType);
            sdpWriter.WriteByte((byte)Constants.SdpServiceName.Length);
            sdpWriter.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
            sdpWriter.WriteString(Constants.SdpServiceName);
            rfcommProvider.SdpRawAttributes.Add(Constants.SdpServiceNameAttributeId, sdpWriter.DetachBuffer());
        }

        private async void OnControllerDataReceived(
            StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            // Don't need the listener anymore
            _controllerSocketListener.Dispose();
            _controllerSocketListener = null;

            try
            {
                _socket = args.Socket;
            }
            catch (Exception e)
            {
                Debugger.Break();
                Disconnect();
                return;
            }

            // Note - this is the supported way to get a Bluetooth device from a given socket
            var remoteDevice = await BluetoothDevice.FromHostNameAsync(_socket.Information.RemoteHostName);

            _writer = new DataWriter(_socket.OutputStream);
            var reader = new DataReader(_socket.InputStream);
            bool remoteDisconnection = false;

            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                ControTextBlock.Text = "连接到" + remoteDevice.Name;
            });

            // Infinite read buffer loop
            while (true)
            {
                try
                {
                    // Based on the protocol we've defined, the first uint is the size of the message
                    uint readLength = await reader.LoadAsync(sizeof(uint));

                    // Check if the size of the data is expected (otherwise the remote has already terminated the connection)
                    if (readLength < sizeof(uint))
                    {
                        remoteDisconnection = true;
                        break;
                    }
                    uint currentLength = reader.ReadUInt32();

                    // Load the rest of the message since you already know the length of the data expected.  
                    readLength = await reader.LoadAsync(currentLength);

                    // Check if the size of the data is expected (otherwise the remote has already terminated the connection)
                    if (readLength < currentLength)
                    {
                        remoteDisconnection = true;
                        break;
                    }
                    string message = reader.ReadString(currentLength);
                    float.Parse(message.Substring(0, 5)); float.Parse(message.Substring(5, 5)); float.Parse(message.Substring(10, 5));
                    //改变视角,message[0]至message[4]为Pitch，message[5]至message[9]为Roll，message[10]至message[14]为Yaw
                }
                // Catch exception HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED).
                catch (Exception ex) when ((uint)ex.HResult == 0x800703E3)
                {
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                        ControTextBlock.Text = "已断开连接";
                    });
                    break;
                }
            }

            reader.DetachStream();
            if (remoteDisconnection)
            {
                Disconnect();
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    ControTextBlock.Text = "未连接";
                });
            }
        }

        private async void Disconnect()
        {
            if (_rfcommProvider != null)
            {
                _rfcommProvider.StopAdvertising();
                _rfcommProvider = null;
            }

            if (_controllerSocketListener != null)
            {
                _controllerSocketListener.Dispose();
                _controllerSocketListener = null;
            }

            if (_writer != null)
            {
                _writer.DetachStream();
                _writer = null;
            }

            if (_socket != null)
            {
                _socket.Dispose();
                _socket = null;
            }
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                ControConnButton.IsEnabled = true;
                //回归正方向
            });
        }
        #endregion

        #region WebSocket连接
        //private void WebSocket_Closed(Windows.Networking.Sockets.IWebSocket sender, Windows.Networking.Sockets.WebSocketClosedEventArgs args)
        //{
        //    Debugger.Break();
        //    //重新连接
        //}

        //private async void ReceiveMessageUsingStreamWebSocket()
        //{
        //    try
        //    {
        //        using (var dataReader = new DataReader(this.streamWebSocket.InputStream))
        //        {
        //            dataReader.InputStreamOptions = InputStreamOptions.Partial;
        //            await dataReader.LoadAsync(256);
        //            byte[] message = new byte[dataReader.UnconsumedBufferLength];
        //            dataReader.ReadBytes(message);
        //            Debug.WriteLine("Data received from StreamWebSocket: " + message.Length + " bytes");
        //        }
        //        this.streamWebSocket.Dispose();
        //    }
        //    catch (Exception ex)
        //    {
        //        Windows.Web.WebErrorStatus webErrorStatus = Windows.Networking.Sockets.WebSocketError.GetStatus(ex.GetBaseException().HResult);
        //        // Add code here to handle exceptions.
        //        Debugger.Break();
        //    }
        //}

        //private async void SendMessageUsingStreamWebSocket(byte[] message)
        //{
        //    try
        //    {
        //        using (var dataWriter = new DataWriter(this.streamWebSocket.OutputStream))
        //        {
        //            dataWriter.WriteBytes(message);
        //            await dataWriter.StoreAsync();
        //            dataWriter.DetachStream();
        //        }
        //        Debug.WriteLine("Sending data using StreamWebSocket: " + message.Length.ToString() + " bytes");
        //    }
        //    catch (Exception ex)
        //    {
        //        Windows.Web.WebErrorStatus webErrorStatus = Windows.Networking.Sockets.WebSocketError.GetStatus(ex.GetBaseException().HResult);
        //        // Add code here to handle exceptions.
        //        Debugger.Break();
        //    }
        //}


        static void ClientConnected(object sender, ClientConnectedEventArgs args)
        {
            Console.WriteLine("Client connected: " + args.IpPort);
        }

        static void ClientDisconnected(object sender, ClientDisconnectedEventArgs args)
        {
            Console.WriteLine("Client disconnected: " + args.IpPort);
        }

        static void SMessageReceived(object sender, MessageReceivedEventArgs args)
        {
            Console.WriteLine("Message received from " + args.IpPort + ": " + Encoding.UTF8.GetString(args.Data));
        }

        static void MessageReceived(object sender, MessageReceivedEventArgs args)
        {
            Console.WriteLine("Message from server: " + Encoding.UTF8.GetString(args.Data));
        }

        static void ServerConnected(object sender, EventArgs args)
        {
            Console.WriteLine("Server connected");
        }

        static void ServerDisconnected(object sender, EventArgs args)
        {
            Console.WriteLine("Server disconnected");
        }

        #endregion


    }


    static class D3DDeviceManager
    {
        private static IDirect3DDevice GlobalDevice;
        public static IDirect3DDevice Device {
            get {
                // This initialization isn't thread safe, so make sure this 
                // happens well before everyone starts needing it.
                if (GlobalDevice == null)
                {
                    GlobalDevice = Direct3D11Helpers.CreateDevice();
                }
                return GlobalDevice;
            }

        }

    }

    class Constants
    {
        // The Chat Server's custom service Uuid: 34B1CF4D-1069-4AD6-89B6-E161D79BE4D8
        public static readonly Guid RfcommChatServiceUuid = Guid.Parse("34B1CF4D-1069-4AD6-89B6-E161D79BE4D8");

        // The Id of the Service Name SDP attribute
        public const UInt16 SdpServiceNameAttributeId = 0x100;

        // The SDP Type of the Service Name SDP attribute.
        // The first byte in the SDP Attribute encodes the SDP Attribute Type as follows :
        //    -  the Attribute Type size in the least significant 3 bits,
        //    -  the SDP Attribute Type value in the most significant 5 bits.
        public const byte SdpServiceNameAttributeType = (4 << 3) | 5;

        // The value of the Service Name SDP attribute
        public const string SdpServiceName = "VirtualMonitorController Service";
    }



    #region 测试
   
    #endregion
}
