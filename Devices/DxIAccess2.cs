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
    public class DxIAccess2 : SerialDevice, IDevice {
        private string mDeviceNo;
        private string mComPort;
        private IDeviceListener mListener;
        private Timer mTimer;

        private int mExpectFrameNo = 1;
        private bool mReceiveETBETX = false;
        private int mRetryCount = 0;
        private DownloadStage mDownloadStage = DownloadStage.Stage_EOT;

        private List<char> mUpBuffer;
        private List<char> mUpFinalBuffer;
        private List<char> mDnBuffer;
        private List<char> mCheckSum;

        private SampleResponse mSampleResponse = null;
        private SampleResult mSampleResult = null;
        private QcResult mQcResult = null;

        private enum TimeOutAction {
            Action_SendENQ,
            Action_SendEOT,
            Action_BackToIdleState,
            Action_RetryWriteData
        }

        private enum ProcessDataResult {
            DataResult_OrderRequest,
            DataResult_TestResult,
            DataResult_QcResult,
            DataResult_None
        }

        private enum SampleRequestResult {
            RequestResult_Found,
            RequestResult_NotFound,
            RequestResult_NoTest
        }

        private enum DownloadStage {
            Stage_ENQ,
            Stage_HeaderInfo,
            Stage_PatientInfo,
            Stage_OrderInfo,
            Stage_TerminateInfo,
            Stage_EOT
        }

        private const int RETRY_LIMIT = 6;

        public DxIAccess2(string DeviceNo, string ComPort, IDeviceListener Listener) : base(ComPort, 9600, Parity.None, 8, StopBits.One) {
            mDeviceNo = DeviceNo;
            mComPort = ComPort;
            mListener = Listener;

            Logger = NLog.LogManager.GetLogger($"DeviceLink.DxIAccess2_{DeviceNo}");
        }

        private void SetTimerTimeout(int second, TimeOutAction action) {
            if (mTimer is null) {
                mTimer = new Timer();
                mTimer.Elapsed += (sender, e) => {
                    if (mTimer.Enabled == false) { return; }
                    switch (action) {
                        case TimeOutAction.Action_SendENQ:
                            WriteData((char)ControlCode.ENQ);
                            break;
                        case TimeOutAction.Action_SendEOT:
                            WriteData((char)ControlCode.EOT);
                            break;
                        case TimeOutAction.Action_RetryWriteData:
                            RetryWriteData();
                            break;
                        case TimeOutAction.Action_BackToIdleState:
                        default:
                            ChangeState(DeviceState.Idle);
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
        }

        public override void Stop() {
            base.Stop();
            ChangeState(DeviceState.Disconnect);
        }

        protected void WriteData(char data, int timeout = 15) {
            mDnBuffer = new List<char>() { data };
            base.WriteData(data);
            mListener.OnDataWriting(mDeviceNo, data.ToPrintOutString());
            SetTimerTimeout(timeout, TimeOutAction.Action_SendEOT);
        }

        protected void WriteData(string data, int timeout = 15) {
            mDnBuffer = data.ToCharArray().ToList();
            base.WriteData(data);
            mListener.OnDataWriting(mDeviceNo, data.ToPrintOutString());
            SetTimerTimeout(timeout, TimeOutAction.Action_SendEOT);
        }

        private void RetryWriteData() {
            if (mRetryCount < RETRY_LIMIT - 1) {
                var message = new string(mDnBuffer.ToArray());
                Logger.Info($"{mRetryCount + 1} time(s) Retry Write Data (Data = {message.ToPrintOutString()})");
                if (message == new string((char)ControlCode.ENQ, 1)) {
                    WriteData(message, 10);
                } else {
                    WriteData(message);
                }
            } else {
                Logger.Info($"Retry Write Data over {RETRY_LIMIT} times, Send [EOT] and Terminate");
                WriteData((char)ControlCode.EOT);
                ChangeState(DeviceState.Idle);
            }
        }
        protected override void ReadData(char chrData) {
            mListener.OnDataReceiving(mDeviceNo, chrData.ToPrintOutString());

            switch (State) {
                case DeviceState.Idle:
                    switch (chrData) {
                        case (char)ControlCode.ENQ:
                            Logger.Info("Receive [ENQ]");
                            ChangeState(DeviceState.Upload);

                            mUpFinalBuffer = new List<char>();

                            SetTimerTimeout(20, TimeOutAction.Action_BackToIdleState);

                            WriteData((char)ControlCode.ACK);
                            break;
                        default:
                            break;
                    }
                    break;
                case DeviceState.Upload:
                    switch (chrData) {
                        case (char)ControlCode.STX:
                            DisableTimer();
                            Logger.Info("Receive [STX]");

                            mUpBuffer = new List<char>();
                            mCheckSum = new List<char>(2);
                            mReceiveETBETX = false;
                            break;
                        case (char)ControlCode.ETX:
                        case (char)ControlCode.ETB:
                            mReceiveETBETX = true;
                            Logger.Info($"Receive {chrData.ToPrintOutString()}");

                            mUpBuffer.Add(chrData);
                            break;
                        case (char)ControlCode.CR_:
                            if (!mReceiveETBETX) { mUpBuffer.Add(chrData); }

                            var success = CheckFrame(mUpBuffer, mCheckSum);
                            if (success) {
                                Logger.Info($"Check Frame Success (Frame = {new string(mUpBuffer.ToArray()).ToPrintOutString()})");

                                mUpFinalBuffer.AddRange(mUpBuffer.SkipLast(1));

                                WriteData((char)ControlCode.ACK);
                            } else {
                                WriteData((char)ControlCode.NAK);
                            }
                            break;
                        case (char)ControlCode.LF_:
                            //Do Nothing
                            break;
                        case (char)ControlCode.EOT:
                            DisableTimer();
                            Logger.Info("Receive [EOT]");

                            if (mUpFinalBuffer.IsNullOrEmpty()) { return; }

                            Logger.Info($"Receive Full Data = {new string(mUpFinalBuffer.ToArray()).ToPrintOutString()}");

                            object outputData;
                            ProcessDataResult processResult = ProcessData(mUpFinalBuffer, out outputData);
                            switch (processResult) {
                                case ProcessDataResult.DataResult_OrderRequest:
                                    ChangeState(DeviceState.Download);

                                    mSampleResponse = mListener.OnSampleRequestReceived(mDeviceNo, (SampleInfo)outputData);

                                    WriteData((char)ControlCode.ENQ);

                                    mDownloadStage = DownloadStage.Stage_ENQ;
                                    break;
                                case ProcessDataResult.DataResult_TestResult:
                                    mListener.OnSampleResultReceived(mDeviceNo, (SampleResult)outputData);

                                    ChangeState(DeviceState.Idle);
                                    break;
                                case ProcessDataResult.DataResult_QcResult:
                                    mListener.OnQcResultReceived(mDeviceNo, (QcResult)outputData);

                                    ChangeState(DeviceState.Idle);
                                    break;
                                case ProcessDataResult.DataResult_None:
                                default:
                                    ChangeState(DeviceState.Idle);
                                    break;
                            }
                            break;
                        default:
                            if (mReceiveETBETX) {
                                mCheckSum.Add(chrData);
                            } else {
                                mUpBuffer.Add(chrData);
                            }
                            break;
                    }
                    break;
                case DeviceState.Download:
                    switch (chrData) {
                        case (char)ControlCode.ACK:
                            DisableTimer();
                            Logger.Info("Receive [ACK]");

                            mDownloadStage = ProcessDownloadData(mSampleResponse, out mDnBuffer);
                            WriteData(new string(mDnBuffer.ToArray()));

                            if (mDownloadStage == DownloadStage.Stage_EOT) {
                                mListener.OnSampleResponseAcknowledged(mDeviceNo, mSampleResponse);
                                ChangeState(DeviceState.Idle);
                            }
                            break;
                        case (char)ControlCode.NAK:
                        default:
                            DisableTimer();
                            Logger.Info("Receive [NAK]");

                            RetryWriteData();
                            break;
                    }
                    break;
            }

        }

        private bool CheckFrame(List<char> data, List<char> checksum) {
            try {
                var strData = new string(data.ToArray());

                var frameNo = strData.Substring(0, 1);
                if (frameNo != mExpectFrameNo.ToString()) { throw new Exception($"Frame No. Mismatched for (Current = {frameNo}, Expect = {mExpectFrameNo})"); }

                var lastTwoChars = strData.TakeLast(2).ToArray();
                if (new string(lastTwoChars) != new string(new char[] { (char)ControlCode.CR_, (char)ControlCode.ETX }) ||
                        new string(lastTwoChars) != new string(new char[] { (char)ControlCode.CR_, (char)ControlCode.ETB })) {
                    throw new Exception($"Frame Strcuture is Corrupted for (Frame = {new string(data.ToArray()).ToPrintOutString()})");
                }

                var calChecksum = ComputeChecksum(data);
                var hexCalChecksum = BitConverter.ToString(new byte[] { (byte)calChecksum });
                var hexChecksum = new string(checksum.ToArray());
                if (hexCalChecksum != hexChecksum) { throw new Exception($"Check Sum Mismatched for (Current = {hexCalChecksum}, Expect = {hexChecksum}"); }

                mExpectFrameNo = (mExpectFrameNo + 1) % 8;

                return true;
            } catch (Exception ex) {
                Logger.Error(ex, $"Check Frame Failed with reason = {ex.Message} !");
                return false;
            }

        }

        private ProcessDataResult ProcessData(List<char> data, out object outputData) {
            var result = ProcessDataResult.DataResult_None;
            outputData = null;

            var record = new string(data.ToArray());

            try {
                var records = record.Split((char)ControlCode.CR_, StringSplitOptions.TrimEntries);
                foreach (var frame in records) {
                    var frameType = frame.Substring(1, 1);
                    var frameOutputString = frame.ToPrintOutString();

                    var fields = frame.Split('|');
                    switch (frameType) {
                        case "H":
                            Logger.Info($"Process Header Info (Data = {frameOutputString})");
                            break;
                        case "Q":
                            Logger.Info($"Process Request Info (Data = {frameOutputString}");

                            outputData = new SampleInfo {
                                SampleID = fields[2].Replace("^", "").Trim(),
                            };
                            result = ProcessDataResult.DataResult_OrderRequest;
                            break;
                        case "P":
                            Logger.Info($"Process Patient Info (Data = {frameOutputString})");
                            break;
                        case "O":
                            Logger.Info($"Process Order Info (Data = {frameOutputString})");

                            outputData = new SampleResult {
                                SampleID = fields[2].Trim(),
                                TestResults = new List<TestResult>()
                            };
                            if (!fields[3].IsNullOrEmpty()) {
                                var components = fields[3].Split("^");
                                var rackNo = components[1].Trim();
                                var cupPos = components[2].Trim();
                                ((SampleResult)outputData).RackNo = rackNo;
                                ((SampleResult)outputData).CupPos = cupPos;
                            }
                            break;
                        case "R":
                            Logger.Info($"Process Result Info (Data = {frameOutputString})");

                            var status = fields[9].Trim();
                            if (status == "F") {
                                var testAttrs = fields[2].Trim().Split("^");
                                var testCode = testAttrs[3].Trim();

                                var valueAttrs = fields[3].Trim().Split("^");
                                var testValue = valueAttrs[0].Trim();

                                var testUnit = fields[4].Trim();

                                var testFlag = fields[7].Trim();

                                var datetimeAttr = fields[13].Trim();
                                var date = datetimeAttr.Substring(0, 8);
                                var time = datetimeAttr.Substring(8, 6);
                                var reportDateTime = new string[] { date, time }.ToAcDateTime();

                                var testResult = new TestResult {
                                    Code = testCode,
                                    Result = testValue,
                                    Flags = testFlag,
                                    Unit = testUnit
                                };
                                ((SampleResult)outputData).ReportDateTime = reportDateTime;
                                ((SampleResult)outputData).TestResults.Add(testResult);
                            }
                            break;
                        case "C":
                            Logger.Info($"Process Comment Info (Data = {frameOutputString})");
                            break;
                        case "L":
                            Logger.Info($"Process Terminate Info (Data = {frameOutputString})");

                            if (((SampleResult)outputData).TestResults.IsNullOrEmpty()) { result = ProcessDataResult.DataResult_None; }
                            break;
                        default:
                            Logger.Info($"Process Other Info (Data = {frameOutputString})");
                            break;
                    }
                }
                return result;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process Data Failed with reason = {ex.Message} !");
                return ProcessDataResult.DataResult_None;
            }
        }

        private DownloadStage ProcessDownloadData(SampleResponse data, out List<char> dnBuffer) {

        }
    }
}
