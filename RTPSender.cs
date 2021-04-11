using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VirtualMonitor_S
{
    class RTPSender
    {
        private string _rtpPackage;//[$],[channel],[size],[size],[RTP Header],[RTP Playload]
        public uint BufSize;
        public struct RTPPackage
        {
            char[] header;
            RTPHeader rtpHeader;
            char[] rtpPayload;
        }
        public struct RTPHeader
        {

        }








    }
}
