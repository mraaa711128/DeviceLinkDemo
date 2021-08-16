using DeviceLink.Extensions;
using DeviceLink.Interface;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeviceLink.Devices
{
    public abstract class SerialDevice
    {
        private SerialPort mSerialPort;
        private DeviceState mState = DeviceState.Unknown;
        public DeviceState State { get => mState; }
        protected abstract void ReadData(char Data);

        protected SerialDevice(string PortName, int BaudRate, Parity ParityCheck, int DataBits, StopBits StopBits)
        {
            mSerialPort = new SerialPort(PortName, BaudRate, ParityCheck, DataBits, StopBits);
            mSerialPort.Encoding = Encoding.Unicode;
            mSerialPort.Handshake = Handshake.RequestToSend;
            mSerialPort.DataReceived += DataReceived;
        }

        protected NLog.Logger Logger { get; set; }

        public virtual void Start()
        {
            mSerialPort.Open();
            Logger.Info($"{Logger.Name} Start Comm with Port {mSerialPort.PortName}");
        }

        public virtual void Stop()
        {
            mSerialPort.Close();
            Logger.Info($"{Logger.Name} Stop Comm with Port {mSerialPort.PortName}");

        }

        protected virtual void DataReceived(object Sender, SerialDataReceivedEventArgs Args)
        {
            if (Args.EventType != SerialData.Chars) { return; }
            while (mSerialPort.BytesToRead >= 1)
            {
                var chr = mSerialPort.ReadByte();
                ReadData((char)chr);
            }
        }

        protected virtual void ChangeState(DeviceState state)
        {
            mState = state;
            Logger.Info($"State Change to {mState.ToString("G")}");
        }

        protected virtual void WriteData(string Data)
        {
            var dataChars = Data.ToCharArray();
            var dataBytes = new byte[] { };
            foreach (var chr in dataChars)
            {
                dataBytes = dataBytes.Append((byte)chr).ToArray();
            }
            mSerialPort.Write(dataBytes, 0, dataBytes.Length);
            Logger.Debug($"Write Data Bytes = {dataBytes}");
            Logger.Info($"Write Data <{Data.ToPrintOutString()}>");
        }

        protected virtual void WriteData(char Data)
        {
            mSerialPort.Write(new byte[] { (byte)Data }, 0, 1);
            Logger.Info($"Write Data <{Data.ToPrintOutString()}>");
        }

        protected char ComputeChecksum(List<char> data)
        {
            byte sum = 0x0;
            foreach (var chr in data)
            {
                sum += (byte)chr;
            }
            sum = (byte)(sum % 256);
            return (char)sum;
        }

        protected char ComputeXOrChecksum(List<char> data)
        {
            byte result = 0x0;
            foreach (var chr in data)
            {
                if (result == 0x0) {
                    result = (byte)chr;
                    continue;
                }
                result = (byte)(result ^ (byte)chr);
            }
            return (char)result;
        }
    }
}
