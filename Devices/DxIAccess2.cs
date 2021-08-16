using DeviceLink.Interface;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeviceLink.Devices
{
    public class DxIAccess2 : SerialDevice
    {
        public DxIAccess2(string DeviceNo, string ComPort, IDeviceListener Listener) : base(ComPort, 9600, Parity.None, 8, StopBits.One)
        {

        }

        protected override void ReadData(char Data)
        {
            throw new NotImplementedException();
        }
    }
}
