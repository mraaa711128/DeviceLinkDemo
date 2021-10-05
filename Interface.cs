using DeviceLink.Structure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeviceLink.Interface
{
    public interface IDevice
    {
        public DeviceState State { get; }
        public void Start();
        public void Stop();
    }
    public interface IDeviceListener
    {
        public void OnDataReceiving(string DeviceNo, string Data);

        public void OnDataWriting(string DeviceNo, string Data);

        public void OnStateChanging(string DeviceNo, DeviceState State);

        public SampleResponse OnSampleRequestReceived(string DeviceNo, SampleInfo Sample);
        public void OnSampleResponseAcknowledged(string DeviceNo, SampleResponse Response);
        public void OnSampleResultReceived(string DeviceNo, SampleResult Result);

        public void OnQcResultReceived(string DeviceNo, QcResult Result);

    }

    public interface ISortingDriveListener {
        public void OnDataReceiving(string Data);
        public void OnDataWriting(string Data);
        public void OnStateChanging(DeviceState State);
        public IList<TubeOrder> OnTubeOrderAcquired();
        public void OnTubeOrderAcknowledged(IList<TubeOrder> TubeOrders);
        public void OnTubeResultReceived(TubeResult Result);
    }

    public enum DeviceState
    {
        Idle,
        Upload,
        Download,
        Disconnect,
        Unknown
    }
}
