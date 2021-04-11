# VirtualMonitor
Make your Mi Cinema Headset  as Virtual Monitors of your Windows PC

CurrentState:

	TCP SOCKET Communicate with Headset via adb forward command：√
	Create Virtual Multi-Desktop via filter driver:√
	Get desktop frame via Windows.Capture api:√
	Encode frame as H264 stream:……

Problem:

	1.Using Windows.Media.Transcode Class to finish stream-encoding job, which is originally desigened to transcode into file
	2.How to reuse the same memory buffer to continually encode and transmit byte data via socket.
