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
using Utility.Extensions;
using Utility.Extensions.Enumerable;

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

        private List<char> mUpBuffer = new List<char>();
        private List<char> mDnBuffer = new List<char>();

        private IList<TubeOrder> mTubeOrders;
        private List<TubeResult> mTubeResults;

        private const int RETRY_LIMIT = 6;

        private enum TimeOutAction {
            //Timeout_TestCom,
            Timeout_AcquireOrders,
            Timeout_GoToIdle
        }
        private enum ProcessDataResult {
            DataResult_StartRecord,
            DataResult_TubeResult,
            DataResult_EndRecord,
            DataResult_None
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

            mTubeOrders = new List<TubeOrder>();

            Logger = NLog.LogManager.GetLogger($"DeviceLink.SortingDrive42_{DeviceNo}");
        }

        private void SetTimerTimeout(int second, TimeOutAction action) {
            if (mTimer is null) {
                mTimer = new Timer();
                mTimer.Elapsed += (sender, e) => {
                    if (mTimer.Enabled == false) { return; }
                    DisableTimer();
                    switch (action) {
                        //case TimeOutAction.Timeout_TestCom:
                        //    ChangeState(DeviceState.Download);
                        //    WriteData((char)ControlCode.STX);
                        //    ChangeState(DeviceState.Idle);
                        //    SetTimerTimeout(3, TimeOutAction.Timeout_TestCom);
                        //    break;
                        case TimeOutAction.Timeout_AcquireOrders:
                            mProcessTubeOrderIndex = 0;
                            mTubeOrders = mListener.OnTubeOrderAcquired();
                            ChangeState(DeviceState.Download);
                            var startRecord = GenerateStartRecord();
                            mDownloadStage = DownloadStage.Stage_StartRecord;
                            WriteData(startRecord);
                            SetTimerTimeout(5, TimeOutAction.Timeout_GoToIdle);
                            break;
                        case TimeOutAction.Timeout_GoToIdle:
                        default:
                            ChangeState(DeviceState.Idle);
                            SetTimerTimeout(10, TimeOutAction.Timeout_AcquireOrders);
                            break;
                    }
                };
            }
            mTimer.Interval = second * 1000;
            mTimer.Start();
        }

        private void DisableTimer() {
            if (mTimer is null) { return; }
            mTimer.Stop();
        }

        public override void Start() {
            base.Start();
            ChangeState(DeviceState.Idle);

            
            SetTimerTimeout(3, TimeOutAction.Timeout_AcquireOrders);
            //SetTimerTimeout(3, TimeOutAction.Timeout_TestCom);
        }

        public override void Stop() {
            base.Stop();
            ChangeState(DeviceState.Disconnect);
            DisableTimer();
        }

        protected override void ChangeState(DeviceState state) {
            base.ChangeState(state);
            mListener.OnStateChanging(mDeviceNo, state);
        }

        protected override void WriteData(char data) {
            mDnBuffer = new List<char>() { data };
            base.WriteData(data);
            mListener.OnDataWriting(mDeviceNo, data.ToPrintOutString());
        }

        protected override void WriteData(string data) {
            mDnBuffer = data.ToCharArray().ToList();
            base.WriteData(data);
            mListener.OnDataWriting(mDeviceNo, data.ToPrintOutString());
        }

        private void RetryWriteData() {
            if (mRetryCount < RETRY_LIMIT - 1) {
                var message = new string(mDnBuffer.ToArray());
                Logger.Info($"{mRetryCount + 1} time(s) Retry Write Data (Data = {message.ToPrintOutString()})");
                WriteData(message);
                SetTimerTimeout(5, TimeOutAction.Timeout_GoToIdle);
                mRetryCount++;
            } else {
                Logger.Info($"Retry Write Data over {RETRY_LIMIT} times, Send End Record and Terminate");
                var endRecord = GenerateEndRecord();
                WriteData(endRecord);
                ChangeState(DeviceState.Idle);
                SetTimerTimeout(10, TimeOutAction.Timeout_AcquireOrders);
            }
        }

        protected override void ReadData(char ChrData) {
            mListener.OnDataReceiving(mDeviceNo, ChrData.ToPrintOutString());
            switch(State) {
                case DeviceState.Idle:
                    switch (ChrData) {
                        case (char)ControlCode.STX:
                            Logger.Info($"Receive [STX]");
                            mReceiveETBETX = false;
                            mReceiveChksum = false;
                            mUpBuffer = new List<char>();
                            mDnBuffer = new List<char>();
                            DisableTimer();
                            ChangeState(DeviceState.Upload);
                            break;
                        default:
                            // Do Nothing
                            break;
                    }
                    break;
                case DeviceState.Upload:
                    if (mReceiveETBETX == false) {
                        switch (ChrData) {
                            case (char)ControlCode.STX:
                                Logger.Info($"Receive [STX]");
                                mReceiveETBETX = false;
                                mReceiveChksum = false;
                                mUpBuffer = new List<char>();
                                mDnBuffer = new List<char>();
                                DisableTimer();
                                break;
                            case (char)ControlCode.ETX:
                                Logger.Info($"Receive [ETX]");
                                mReceiveETBETX = true;
                                mUpBuffer.Add(ChrData);
                                break;
                            default:
                                    mUpBuffer.Add(ChrData);
                                break;
                        }

                    }
                    if (mReceiveChksum == false) {
                        var chrCheckSum = ChrData;
                        Logger.Info($"Recieve CheckSum = {chrCheckSum} ({BitConverter.ToString(new byte[] { (byte)chrCheckSum })})");
                        mReceiveChksum = true;

                        var computeChecksum = ComputeXOrChecksum(mUpBuffer);
                        if (computeChecksum != chrCheckSum) {
                            Logger.Info($"Frame Check Failed because Checksum Compare Failed Received = {chrCheckSum}, Computed = {computeChecksum}");
                            WriteData((char)ControlCode.NAK);
                            return;
                        } else {
                            Logger.Info($"Verify Checksum Success (Data = {new string(mUpBuffer.ToArray()).ToPrintOutString()})");
                            WriteData((char)ControlCode.ACK);
                        }

                        TubeResult tubeResult = null;
                        ProcessDataResult result = ProcessData(new string(mUpBuffer.ToArray()), out tubeResult);
                        switch (result) {
                            case ProcessDataResult.DataResult_TubeResult:
                                mListener.OnTubeResultReceived(tubeResult);
                                break;
                            case ProcessDataResult.DataResult_EndRecord:
                                ChangeState(DeviceState.Idle);
                                SetTimerTimeout(10, TimeOutAction.Timeout_AcquireOrders);
                                break;
                            case ProcessDataResult.DataResult_StartRecord:
                            case ProcessDataResult.DataResult_None:
                            default:
                                break;
                        }

                        mReceiveETBETX = false;
                        mReceiveChksum = false;
                    }
                    break;
                case DeviceState.Download:
                    switch (ChrData) {
                        case (char)ControlCode.ACK:
                            Logger.Info("Receive [ACK]");
                            DisableTimer();

                            mDownloadStage = ProcessDownloadData(mTubeOrders, out mDnBuffer);

                            if (mDownloadStage == DownloadStage.Stage_Done) {
                                mListener.OnTubeOrderAcknowledged(mTubeOrders);
                                mTubeOrders.Clear();
                                ChangeState(DeviceState.Idle);
                                SetTimerTimeout(10, TimeOutAction.Timeout_AcquireOrders);
                            } else {
                                WriteData(new string(mDnBuffer.ToArray()));
                                SetTimerTimeout(5, TimeOutAction.Timeout_GoToIdle);
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
            result = null;

            var recType = data.Substring(0, 1);

            try {
                switch (recType) {
                    case "S":
                        Logger.Info($"Process Start Record (Data = {data.Substring(0, data.Length - 1).ToPrintOutString()})");
                        return ProcessDataResult.DataResult_StartRecord;
                    case "E":
                        Logger.Info($"Process End Record (Data = {data.Substring(0, data.Length - 1).ToPrintOutString()})");
                        return ProcessDataResult.DataResult_EndRecord;
                    case "R":
                        var fields = data.Split("|");
                        Logger.Info($"Process Tube Record (Data = {data.Substring(0, data.Length - 1).ToPrintOutString()})");
                        var date = fields[11].Trim().Substring(0, 8);
                        var time = fields[11].Trim().Substring(9, 6);
                        result = new TubeResult {
                            SampleID = fields[3].Trim(),
                            SampleType = fields[7].Trim(),
                            TubeType = int.Parse(fields[5].Trim()),
                            WorkplaceType = int.Parse(fields[6].Trim()),
                            ArchiveID = fields[8].Trim(),
                            RackID = fields[9].Trim(),
                            RowID = fields[10].Trim().Substring(0, 2),
                            ColumnID = fields[10].Trim().Substring(2, 2),
                            ReportDateTime = new string[] { date, time }.ToAcDateTime(),
                            Volume = int.Parse(fields[12].Trim()),
                            TestOrders = fields[13].Split("~").Select(f => new TestOrder {
                                Code = f,
                                Dilution = SampleDilution.None
                            }).ToList()
                        };
                        return ProcessDataResult.DataResult_TubeResult;
                    case "T":
                        Logger.Info($"Process Detect Record (Data = {data.Substring(0, data.Length - 1).ToPrintOutString()})");
                        return ProcessDataResult.DataResult_None;
                    default:
                        return ProcessDataResult.DataResult_None;
                }
            } catch (Exception ex) {
                Logger.Error(ex, $"Process Data Failed with reason = {ex.Message} !");
                return ProcessDataResult.DataResult_None;
            }
        }

        private DownloadStage ProcessDownloadData(IList<TubeOrder> orders, out List<char> dnBuffer) {
            var result = DownloadStage.Stage_StartRecord;
            dnBuffer = null;
            TubeOrder order = null;

            switch(mDownloadStage) {
                case DownloadStage.Stage_StartRecord:
                case DownloadStage.Stage_TubeOrder:
                    if (mProcessTubeOrderIndex < orders.Count) {
                        order = orders[mProcessTubeOrderIndex];
                        mProcessTubeOrderIndex++;

                        dnBuffer = $"O|LIS|{order.SampleID}||{(order.IsEmergency ? "1" : "0")}|0||||||||||".ToList();
                        var testBuffer = new List<char>();
                        foreach (var test in order.TestOrders) {
                            if (testBuffer.IsNullOrEmpty()) {
                                testBuffer.AddRange($"{test.Code}".ToList());
                            } else {
                                testBuffer.AddRange($"~{test.Code}".ToList());
                            }
                        }
                        dnBuffer.AddRange(testBuffer);

                        result = DownloadStage.Stage_TubeOrder;
                    } else {
                        dnBuffer = $"E|||||||||||||||".ToList();
                        result = DownloadStage.Stage_EndRecord;
                    }
                    break;
                case DownloadStage.Stage_EndRecord:
                    dnBuffer = null;
                    return DownloadStage.Stage_Done;
                case DownloadStage.Stage_Done:
                default:
                    Logger.Error("Unexpected Behavior from Device DxI Access2");
                    dnBuffer = null;
                    return DownloadStage.Stage_Done;
            }

            Logger.Info($"Data = {new string(dnBuffer.ToArray()).ToPrintOutString()}");

            dnBuffer.Insert(0, (char)ControlCode.STX);
            dnBuffer.Add((char)ControlCode.ETX);

            var chrChecksum = ComputeXOrChecksum(dnBuffer.Skip(1).ToList());
            dnBuffer.Add(chrChecksum);

            Logger.Info($"Computed Data = {new string(dnBuffer.ToArray()).ToPrintOutString()}");

            return result;
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
