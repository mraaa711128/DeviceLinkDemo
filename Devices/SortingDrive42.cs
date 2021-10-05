using DeviceLink.Const;
using DeviceLink.Extensions;
using DeviceLink.Interface;
using DeviceLink.Structure;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;

namespace DeviceLink.Devices {
    public class SortingDrive42 : SerialDevice, IDevice {
        private string mDeviceNo;
        private string mComPort;
        private ISortingDriveListener mListener;
        private Timer mTimer;

        private bool mReceiveETBETX = false;
        private bool mReceiveChksum = false;
        private int mRetryCount = 0;
        private DownloadStage mDownloadStage = DownloadStage.Stage_Done;
        private int mProcessTubeOrderIndex = 0;

        private List<char> mUpBuffer;
        private List<char> mDnBuffer;

        private List<TubeOrder> mTubeOrders;
        private List<TubeResult> mTubeResults;

        private const int RETRY_LIMIT = 6;

        private enum ProcessDataResult {
            DataResult_StartRecord,
            DataResult_TubeResult,
            DataResult_EndRecord
        }
        private enum DownloadStage {
            Stage_StartRecord,
            Stage_TubeOrder,
            Stage_EndRecord,
            Stage_Done
        }

        public SortingDrive42(string DeviceNo, string ComPort, ISortingDriveListener Listener) : base(ComPort, 9600, Parity.None, 8, StopBits.One) {
            mDeviceNo = DeviceNo;
            mComPort = ComPort;
            mListener = Listener;

            Logger = NLog.LogManager.GetLogger($"DeviceLink.SortingDrive42_{DeviceNo}");
        }

        public override void Start() {
            base.Start();
            ChangeState(DeviceState.Idle);
        }

        public override void Stop() {
            base.Stop();
            ChangeState(DeviceState.Disconnect);
        }

        protected override void ChangeState(DeviceState state) {
            base.ChangeState(state);
            mListener.OnStateChanging(state);
        }

        protected override void WriteData(char data) {
            mDnBuffer = new List<char>() { data };
            base.WriteData(data);
            mListener.OnDataWriting(data.ToPrintOutString());
        }

        protected override void WriteData(string data) {
            mDnBuffer = data.ToCharArray().ToList();
            base.WriteData(data);
            mListener.OnDataWriting(data.ToPrintOutString());
        }

        private void RetryWriteData() {
            if (mRetryCount < RETRY_LIMIT - 1) {
                var message = new string(mDnBuffer.ToArray());
                Logger.Info($"{mRetryCount + 1} time(s) Retry Write Data (Data = {message.ToPrintOutString()})");
                WriteData(message);
                mRetryCount++;
            } else {
                Logger.Info($"Retry Write Data over {RETRY_LIMIT} times, Send End Record and Terminate");
                var endRecord = GenerateEndRecord();
                WriteData(endRecord);
                ChangeState(DeviceState.Idle);
            }
        }

        protected override void ReadData(char ChrData) {
            mListener.OnDataReceiving(ChrData.ToPrintOutString());
            switch(State) {
                case DeviceState.Idle:
                    switch (ChrData) {
                        case (char)ControlCode.STX:
                            Logger.Info($"Receive [STX]");
                            mReceiveETBETX = false;
                            mReceiveChksum = false;
                            mUpBuffer.Clear();
                            mDnBuffer.Clear();
                            ChangeState(DeviceState.Upload);
                            break;
                        default:
                            break;
                    }
                    break;
                case DeviceState.Upload:
                    switch(ChrData) {
                        case (char)ControlCode.STX:
                            Logger.Info($"Receive [STX]");
                            mReceiveETBETX = false;
                            mReceiveChksum = false;
                            mUpBuffer.Clear();
                            mDnBuffer.Clear();
                            break;
                        case (char)ControlCode.ETX:
                            Logger.Info($"Receive [ETX]");
                            mReceiveETBETX = true;
                            mUpBuffer.Add(ChrData);
                            break;
                        default:
                            if (mReceiveETBETX == false) {
                                mUpBuffer.Add(ChrData);
                            } else {
                                var chrCheckSum = ChrData;
                                Logger.Info($"Recieve CheckSum = {chrCheckSum} ({BitConverter.ToString(new byte[] { (byte)chrCheckSum })})");
                                mReceiveChksum = true;

                                var computeChecksum = ComputeXOrChecksum(mUpBuffer);
                                if (computeChecksum != chrCheckSum) {
                                    Logger.Info($"Frame Check Failed because Checksum Compare Failed Received = {chrCheckSum}, Computed = {computeChecksum}");
                                    WriteData((char)ControlCode.NAK);
                                    return;
                                }

                                TubeResult tubeResult = null;
                                ProcessDataResult result = ProcessData(new string(mUpBuffer.ToArray()), out tubeResult);
                                switch(result) {
                                    case ProcessDataResult.DataResult_TubeResult:
                                        mListener.OnTubeResultReceived(tubeResult);
                                        break;
                                    case ProcessDataResult.DataResult_EndRecord:
                                        ChangeState(DeviceState.Idle);
                                        break;
                                    case ProcessDataResult.DataResult_StartRecord:
                                    default:
                                        break;
                                }
                            }
                            break;
                    }
                    break;
                case DeviceState.Download:
                    switch (ChrData) {
                        case (char)ControlCode.ACK:
                            Logger.Info("Receive [ACK]");

                            mDownloadStage = ProcessDownloadData(mTubeOrders, out mDnBuffer);
                            WriteData(new string(mDnBuffer.ToArray()));

                            if (mDownloadStage == DownloadStage.Stage_Done) {
                                mListener.OnTubeOrderAcknowledged(mTubeOrders);
                                ChangeState(DeviceState.Idle);
                            }
                            break;
                        case (char)ControlCode.NAK:
                        default:
                            Logger.Info($"Receive {ChrData.ToPrintOutString()}");

                            RetryWriteData();
                            break;
                    }
                    break;
                default:
                    break;
            }
        }

        private ProcessDataResult ProcessData(string data, out TubeResult result) {

        }

        private DownloadStage ProcessDownloadData(IList<TubeOrder> orders, out List<char> dnBuffer) {

        }
        private string GenerateStartRecord() {
            var record = "S|||||||||||||||".ToList();
            record.Insert(0, (char)ControlCode.STX);
            record.Add((char)ControlCode.ETX);

            var checkSum = ComputeXOrChecksum(record.Skip(1).ToList());
            record.Add(checkSum);
            return new string(record.ToArray());
        }

        private string GenerateEndRecord() {
            var record = "E|||||||||||||||".ToList();
            record.Insert(0, (char)ControlCode.STX);
            record.Add((char)ControlCode.ETX);

            var checkSum = ComputeXOrChecksum(record.Skip(1).ToList());
            record.Add(checkSum);
            return new string(record.ToArray());
        }
    }
}
