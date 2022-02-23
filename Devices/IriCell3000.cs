using DeviceLink.Const;
using DeviceLink.Interface;
using DeviceLink.Structure;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Timers;
using System.Threading.Tasks;
using System.Xml;
using Utility.Extensions;
using DeviceLink.Extensions;
using Utility.Extensions.Enumerable;

namespace DeviceLink.Devices {
    public class IriCell3000 : SerialDevice, IDevice {
        private string mDeviceNo;
        private string mComPort;
        private IDeviceListener mListener;
        private Timer mTimer;

        private int mExpectFrameNo = 1;
        private bool mReceiveETBETX = false;
        private int mRetryCount = 0;
        private int mRetryLimit = 6;
        private DownloadStage mDownloadStage = DownloadStage.Stage_EOT;

        private List<char> mUpBuffer;
        private List<char> mUpFinalBuffer;
        private List<char> mDnBuffer;
        private List<char> mDnFinalBuffer;
        private List<char> mCheckSum;

        private SampleResponse mSampleResponse = null;

        private enum TimeOutAction {
            Action_SendENQ,
            Action_SendEOT,
            Action_BackToIdleState,
            Action_RetryWriteData
        }

        protected enum ProcessDataResult {
            DataResult_IRISPing,
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
            Stage_PingResponse,
            Stage_UnknownResponse,
            Stage_OrderResponse,
            Stage_TerminateResponse,
            Stage_EOT
        }

        public IriCell3000(string DeviceNo, string ComPort, IDeviceListener Listener) : base(ComPort, 9600, Parity.None, 8, StopBits.One) {
            mDeviceNo = DeviceNo;
            mComPort = ComPort;
            mListener = Listener;

            Logger = NLog.LogManager.GetLogger($"DeviceLink.IriCell3000_{DeviceNo}");
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

        protected override void ChangeState(DeviceState state) {
            base.ChangeState(state);
            mListener.OnStateChanging(mDeviceNo, state);
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
            if (mRetryCount < mRetryLimit - 1) {
                var message = new string(mDnBuffer.ToArray());
                Logger.Info($"{mRetryCount + 1} time(s) Retry Write Data (Data = {message.ToPrintOutString()})");
                if (message == new string((char)ControlCode.ENQ, 1)) {
                    WriteData(message, 10);
                } else {
                    WriteData(message);
                }
                mRetryCount++;
            } else {
                Logger.Info($"Retry Write Data over {mRetryLimit} times, Send [EOT] and Terminate");
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

                            mExpectFrameNo = 1;
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
                            if (mReceiveETBETX) {
                                var success = CheckFrame(mUpBuffer, mCheckSum);
                                if (success) {
                                    Logger.Info($"Check Frame Success (Frame = {new string(mUpBuffer.ToArray()).ToPrintOutString()})");

                                    mUpFinalBuffer.AddRange(mUpBuffer.Skip(1).SkipLast(1));

                                    WriteData((char)ControlCode.ACK);
                                } else {
                                    WriteData((char)ControlCode.NAK);
                                }
                            } else {
                                mUpBuffer.Add(chrData);
                            }

                            break;
                        case (char)ControlCode.LF_:
                            //Do Nothing
                            break;
                        case (char)ControlCode.EOT:
                            DisableTimer();
                            Logger.Info("Receive [EOT]");

                            if (mUpFinalBuffer.IsNullOrEmpty()) {
                                ChangeState(DeviceState.Idle);
                                return;
                            }

                            Logger.Info($"Receive Full Data = {new string(mUpFinalBuffer.ToArray()).ToPrintOutString()}");

                            object outputData;
                            ProcessDataResult processResult = ProcessData(mUpFinalBuffer, out outputData);
                            switch (processResult) {
                                case ProcessDataResult.DataResult_IRISPing:
                                    ChangeState(DeviceState.Download);

                                    Logger.Info("Send [ENQ] to Start IRISPing");
                                    WriteData((char)ControlCode.ENQ);

                                    mDownloadStage = DownloadStage.Stage_PingResponse;
                                    break;
                                case ProcessDataResult.DataResult_OrderRequest:
                                    ChangeState(DeviceState.Download);

                                    mSampleResponse = mListener.OnSampleRequestReceived(mDeviceNo, (SampleInfo)outputData);
                                    if (mSampleResponse is not null) {
                                        Logger.Info("Send [ENQ] to Start Order Response");
                                        WriteData((char)ControlCode.ENQ);

                                        mDownloadStage = DownloadStage.Stage_OrderResponse;
                                    } else {
                                        Logger.Info("Send [ENQ] to Start Unknown Response");
                                        WriteData((char)ControlCode.ENQ);

                                        mDownloadStage = DownloadStage.Stage_UnknownResponse;
                                    }
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

                var lastChar = strData.TakeLast(1).ToArray();
                if (new string(lastChar) != new string(new char[] { (char)ControlCode.ETX }) &&
                        new string(lastChar) != new string(new char[] { (char)ControlCode.ETB })) {
                    throw new Exception($"Frame Strcuture is Corrupted for (Frame = {new string(data.ToArray()).ToPrintOutString()}, LastTwoChars = {new string(lastChar).ToPrintOutString()})");
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
                var frameText = record.FromHexToUnicode();
                var recordXml = new XmlDocument();
                recordXml.LoadXml(frameText);

                var root = recordXml.DocumentElement;
                if (root is null) { throw new XmlException("Record Data is incomplete or undefined !"); }
                bool success = false;

                switch (root.Name) {
                    case "IRISPing":
                        return ProcessDataResult.DataResult_IRISPing;
                    case "SIQ":
                        string sampleID = null;
                        success = ProcessRequestInfo(root, out sampleID);
                        if (success) {
                            outputData = new SampleInfo {
                                SampleID = sampleID
                            };
                            result = ProcessDataResult.DataResult_OrderRequest;
                        }
                        break;
                    case "SA":
                        SampleResult sampleResult = null;
                        success = ProcessSampleResult(root, out sampleResult);
                        if (success) {
                            outputData = sampleResult;
                            result = ProcessDataResult.DataResult_TestResult;
                        }
                        break;
                    case "ChemQC":
                        QcResult chemQcResult = null;
                        success = ProcessChemQCResult(root, out chemQcResult);
                        if (success) {
                            outputData = chemQcResult;
                            result = ProcessDataResult.DataResult_QcResult;
                        }
                        break;
                    case "CQC":
                        QcResult cQcResult = null;
                        success = ProcessCountQCResult(root, out cQcResult);
                        if (success) {
                            outputData = cQcResult;
                            result = ProcessDataResult.DataResult_QcResult;
                        }
                        break;
                    case "BFQC":
                        QcResult bfQcResult = null;
                        success = ProcessBodyFluidQCResult(root, out bfQcResult);
                        if (success) {
                            outputData = bfQcResult;
                            result = ProcessDataResult.DataResult_QcResult;
                        }
                        break;
                    default:
                        break;
                }
                return result;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process Data Failed with Reason ({ex.Message}) !");
                return ProcessDataResult.DataResult_None;
            }
        }

        private DownloadStage ProcessDownloadData(SampleResponse data, out List<char> dnBuffer) {
            var result = DownloadStage.Stage_ENQ;
            var success = false;
            var hasMore = (!mDnFinalBuffer.IsNullOrEmpty());
            dnBuffer = null;


            if (hasMore == false) {
                mExpectFrameNo = 1;
                XmlDocument xmlDoc = null;

                switch (mDownloadStage) {
                    case DownloadStage.Stage_PingResponse:
                        xmlDoc = CreatePingResponse();
                        break;
                    case DownloadStage.Stage_OrderResponse:
                        xmlDoc = CreateOrderResponse(data);
                        break;
                    case DownloadStage.Stage_UnknownResponse:
                        xmlDoc = CreateUnknownResponse(data);
                        break;
                    default:
                        if (mDownloadStage != DownloadStage.Stage_TerminateResponse) {
                            Logger.Error("Unexpected behavior in Process Download Data !");

                        }
                        dnBuffer = new List<char>() { (char)ControlCode.EOT };

                        return DownloadStage.Stage_EOT;
                }
                if (xmlDoc is null) {
                    Logger.Error("Process Download Data Failed with Reason (Create corresponding response failed) !");
                    return DownloadStage.Stage_TerminateResponse; 
                }
                mDnFinalBuffer = xmlDoc.OuterXml.FromUnicodeToHex().ToList();
            }
            dnBuffer = mDnFinalBuffer.Take(240).ToList();
            if (mDnFinalBuffer.Count > 240) {
                hasMore = true;
                mDnFinalBuffer = mDnFinalBuffer.Skip(240).ToList();
            } else {
                hasMore = false;
                mDnFinalBuffer.Clear();
                switch (mDownloadStage) {
                    case DownloadStage.Stage_PingResponse:
                    case DownloadStage.Stage_OrderResponse:
                    case DownloadStage.Stage_UnknownResponse:
                        result = DownloadStage.Stage_TerminateResponse;
                        break;
                    case DownloadStage.Stage_TerminateResponse:
                    case DownloadStage.Stage_EOT:
                    default:
                        Logger.Error("Unexpected behavior in Process Download Data (It shall not execute to here) !");
                        dnBuffer = null;
                        return DownloadStage.Stage_EOT;
                }
            }

            if (!dnBuffer.IsNullOrEmpty()) {
                Logger.Info($"Download Data = {new string(dnBuffer.ToArray()).ToPrintOutString()}");

                dnBuffer.InsertRange(0, new char[] { (char)ControlCode.STX, Convert.ToChar(mExpectFrameNo.ToString("0")) });
                if (hasMore) {
                    dnBuffer.AddRange(new char[] { (char)ControlCode.ETB });
                } else {
                    dnBuffer.AddRange(new char[] { (char)ControlCode.ETX });
                }

                var chrChecksum = ComputeChecksum(dnBuffer.Skip(1).ToList());
                var hexChecksum = BitConverter.ToString(new byte[] { (byte)chrChecksum });
                dnBuffer.AddRange(hexChecksum.ToList());
                dnBuffer.AddRange(new char[] { (char)ControlCode.CR_, (char)ControlCode.LF_ });

                Logger.Info($"Computed Download Data = {new string(dnBuffer.ToArray()).ToPrintOutString()}");

                mExpectFrameNo = (mExpectFrameNo + 1) % 8;
            }

            return result;
        }

        private bool ProcessRequestInfo(XmlElement xml, out string SampleID) {
            SampleID = null;
            try {
                SampleID = xml.InnerText;
                return true;
            } catch (Exception ex) {
                Logger.Error($"Process Request Info Failed with Reason ({ex.Message}) !");
                return false;
            }
        }

        private bool ProcessSampleResult(XmlElement xml, out SampleResult sampleResult) {
            sampleResult = null;
            try {
                var sampleID = xml.GetAttribute("ID");
                var datetime = xml.GetAttribute("ADTS");
                var rackNo = xml.GetAttribute("RAQN");
                var cupPos = xml.GetAttribute("RP");
                var sampleType = xml.GetAttribute("BF");
                var reportDateTime = new string[] { datetime.Substring(0, 10).Replace("-", ""), datetime.Substring(datetime.Length - 8, 8).Replace(":", "") }.ToAcDateTime();

                sampleResult = new SampleResult {
                    SampleID = sampleID,
                    ReportDateTime = reportDateTime,
                    RackNo = rackNo,
                    CupPos = cupPos,
                    SampleType = sampleType,
                    TestResults = new List<TestResult>()
                };

                var ACs = xml.SelectNodes("/SA//AC");
                if (ACs.IsNullOrEmpty()) { throw new Exception("There is no legal element <AC> !"); }
                
                for(var i = 0; i < ACs.Count; i++) {
                    var AC = ACs.Item(i);
                    var valAT = AC.Attributes["AT"].Value;
                    var valAS = AC.Attributes["AS"].Value;
                    if (valAS == "Done") {
                        var ARs = AC.SelectNodes($"//AC[@AT='{valAT}']//AR");
                        if (ARs.IsNullOrEmpty()) { continue; }
                        for (var j = 0; j < ARs.Count; j++) {
                            var AR = ARs.Item(j);
                            var valKey = AR.Attributes["Key"].Value;
                            
                            var valFlag = AR.Attributes["AF"].Value;
                            if (valFlag == "0") { valFlag = ""; }
                            
                            var results = AR.InnerText.Split(' ');
                            if (results.IsNullOrEmpty()) { continue; }
                            var valResult = results[0];
                            if (valResult.ToUpper() == "[none]") { valResult = ""; }

                            var testResult = new TestResult {
                                Code = valKey,
                                Flags = valFlag,
                                Result = valResult
                            };

                            if (results.Length >= 2) { testResult.Unit = results[1]; }

                            if (valKey == "URO" && (valResult.ToUpper() == "NEGATIVE" || valResult == "-")) {
                                testResult.Result = "0.2";
                                testResult.Unit = "mg/dL";
                            }

                            sampleResult.TestResults.Add(testResult);
                        }
                    }
                }
                return true;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process Sample Result Failed with Reason ({ex.Message}) !");
                return false;
            }
        }

        private bool ProcessChemQCResult(XmlElement xml, out QcResult qcResult) {
            qcResult = null;
            try {
                var qcSampleID = xml.GetAttribute("NAME");
                var lotNo = xml.GetAttribute("LOT");
                var datetime = xml.GetAttribute("ADTS");
                var reportDateTime = new string[] { datetime.Substring(0, 10).Replace("-", ""), datetime.Substring(datetime.Length - 8, 8).Replace(":", "") }.ToAcDateTime();
                var status = xml.GetAttribute("STATUS");

                if (status == "F") { throw new Exception("ChemQC Status is Fail !"); }

                qcResult = new QcResult {
                    SampleID = qcSampleID,
                    ControlNo = lotNo,
                    ReportDateTime = reportDateTime,
                    QcResults = new List<TestResult>()
                };

                var QCRs = xml.SelectNodes("/ChemQC//QCR");
                if (QCRs.IsNullOrEmpty()) { throw new Exception("There is no legal element <QCR> !"); }

                for(var i = 0; i < QCRs.Count; i++) {
                    var QCR = QCRs.Item(i);
                    var valStatus = QCR.Attributes["ISTATUS"].Value;
                    if (valStatus == "P") {
                        var valKey = QCR.Attributes["KEY"].Value;
                        var valUnit = QCR.Attributes["UNIT"].Value;
                        var valText = QCR.InnerText;
                        var valResult = "";

                        if (valText.IndexOf("(") >= 0 && valText.IndexOf(")") >= 0) {
                            // Get +/-
                            valResult = new string(valText.Take(valText.IndexOf("(") + 1).ToArray());
                            // Get Numeric Value
                            //var valResults = valText.Substring(valText.IndexOf("(") + 1, valText.IndexOf(")") - valText.IndexOf("(") - 1).Split(" ");
                            //if (valResults.IsNullOrEmpty()) { continue; }
                            //valResult = valResults[0].Trim();
                        } else {
                            valResult = valText.Trim();
                        }

                        if (valResult.ToUpper() == "NEGATIVE") { valResult = "-"; }

                        var qcTest = new TestResult {
                            Code = valKey,
                            Unit = valUnit,
                            Result = valResult
                        };

                        qcResult.QcResults.Add(qcTest);
                    }
                }
                return true;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process ChemQC Result Failed with Reason ({ex.Message}) !");
                return false;
            }
        }

        private bool ProcessCountQCResult(XmlElement xml, out QcResult qcResult) {
            qcResult = null;
            try {
                var qcSampleID = xml.GetAttribute("CCType");
                var lotNo = xml.GetAttribute("LOT");
                var datetime = xml.GetAttribute("ADTS");
                var reportDateTime = new string[] { datetime.Substring(0, 10).Replace("-", ""), datetime.Substring(datetime.Length - 8, 8).Replace(":", "") }.ToAcDateTime();

                var STATUS = xml.SelectSingleNode("/CQC//STATUS");
                if (STATUS is null) { throw new Exception("There is no legal element <STATUS> in <CQC> !"); }

                var valStatus = STATUS.InnerText.Trim();

                if (valStatus == "F") { throw new Exception("CountQC Status is Fail !"); }

                qcResult = new QcResult {
                    SampleID = qcSampleID,
                    ControlNo = lotNo,
                    ReportDateTime = reportDateTime,
                    QcResults = new List<TestResult>()
                };

                var meanQcTest = new TestResult {
                    Code = "MEAN",
                    Result = xml.GetAttribute("MEAN")
                };
                qcResult.QcResults.Add(meanQcTest);

                var mncQcTest = new TestResult {
                    Code = "MNC",
                    Result = xml.GetAttribute("MNC")
                };
                qcResult.QcResults.Add(mncQcTest);

                var MIN = xml.SelectSingleNode("/CQC//MIN");
                if (MIN is null) { throw new Exception("There is no legal element <MIN> in <CQC> !"); }
                var minQcTest = new TestResult {
                    Code = "MIN",
                    Result = MIN.InnerText.Trim()
                };
                qcResult.QcResults.Add(minQcTest);

                var MAX = xml.SelectSingleNode("/CQC//MAX");
                if (MAX is null) { throw new Exception("There is no legal element <MAX> in <CQC> !"); }
                var maxQcTest = new TestResult {
                    Code = "MAX",
                    Result = MAX.InnerText.Trim()
                };
                qcResult.QcResults.Add(maxQcTest);

                return true;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process CQC Result Failed with Reason ({ex.Message}) !");
                return false;
            }
        }

        private bool ProcessBodyFluidQCResult (XmlElement xml, out QcResult qcResult) {
            qcResult = null;
            try {
                var qcSampleID = xml.GetAttribute("BFCType");
                var lotNo = xml.GetAttribute("LOT");
                var datetime = xml.GetAttribute("ADTS");
                var reportDateTime = new string[] { datetime.Substring(0, 10).Replace("-", ""), datetime.Substring(datetime.Length - 8, 8).Replace(":", "") }.ToAcDateTime();

                var STATUS = xml.SelectSingleNode("/BFQC//STATUS");
                if (STATUS is null) { throw new Exception("There is no legal element <STATUS> in <BFQC> !"); }

                var valStatus = STATUS.InnerText.Trim();

                if (valStatus == "F") { throw new Exception("BodyFluidQC Status is Fail !"); }

                qcResult = new QcResult {
                    SampleID = qcSampleID,
                    ControlNo = lotNo,
                    ReportDateTime = reportDateTime,
                    QcResults = new List<TestResult>()
                };

                var rbcMeanQcTest = new TestResult {
                    Code = "RBCMEAN",
                    Result = xml.GetAttribute("RBCMEAN")
                };
                qcResult.QcResults.Add(rbcMeanQcTest);

                var nucMeanQcTest = new TestResult {
                    Code = "NUCMEAN",
                    Result = xml.GetAttribute("NUCMEAN")
                };
                qcResult.QcResults.Add(nucMeanQcTest);

                var RBCMIN = xml.SelectSingleNode("/BFQC//RBCMIN");
                if (RBCMIN is null) { throw new Exception("There is no legal element <RBCMIN> in <BFQC> !"); }
                var rbcMinQcTest = new TestResult {
                    Code = "RBCMIN",
                    Result = RBCMIN.InnerText.Trim()
                };
                qcResult.QcResults.Add(rbcMinQcTest);

                var RBCMAX = xml.SelectSingleNode("/BFQC//RBCMAX");
                if (RBCMAX is null) { throw new Exception("There is no legal element <RBCMAX> in <BFQC> !"); }
                var rbcMaxQcTest = new TestResult {
                    Code = "RBCMAX",
                    Result = RBCMAX.InnerText.Trim()
                };
                qcResult.QcResults.Add(rbcMaxQcTest);

                var RBCCOUNT = xml.SelectSingleNode("/BFQC//RBCCOUNT");
                if (RBCCOUNT is null) { throw new Exception("There is no legal element <RBCCOUNT> in <BFQC> !"); }
                var rbcCountQcTest = new TestResult {
                    Code = "RBCCOUNT",
                    Result = RBCCOUNT.InnerText.Trim()
                };
                qcResult.QcResults.Add(rbcCountQcTest);

                var NUCMIN = xml.SelectSingleNode("/BFQC//NUCMIN");
                if (NUCMIN is null) { throw new Exception("There is no legal element <NUCMIN> in <BFQC> !"); }
                var nucMinQcTest = new TestResult {
                    Code = "NUCMIN",
                    Result = NUCMIN.InnerText.Trim()
                };
                qcResult.QcResults.Add(nucMinQcTest);

                var NUCMAX = xml.SelectSingleNode("/BFQC//NUCMAX");
                if (NUCMAX is null) { throw new Exception("There is no legal element <NUCMAX> in <BFQC> !"); }
                var nucMaxQcTest = new TestResult {
                    Code = "NUCMAX",
                    Result = NUCMAX.InnerText.Trim()
                };
                qcResult.QcResults.Add(nucMaxQcTest);

                var NUCCOUNT = xml.SelectSingleNode("/BFQC//NUCCOUNT");
                if (NUCCOUNT is null) { throw new Exception("There is no legal element <NUCCOUNT> in <BFQC> !"); }
                var nucCountQcTest = new TestResult {
                    Code = "NUCCOUNT",
                    Result = NUCCOUNT.InnerText.Trim()
                };
                qcResult.QcResults.Add(nucCountQcTest);

                return true;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process BodyFluidQC Result Failed with Reason ({ex.Message}) !");
                return false;
            }
        }

        private XmlDocument CreatePingResponse() {
            try {
                var doc = new XmlDocument();
                var IRISPing = doc.CreateElement("IRISPing");
                doc.AppendChild(IRISPing);
                return doc;
            } catch (Exception ex) {
                Logger.Error(ex, $"Create Ping Response Failed with Reason ({ex.Message}) !");
                return null;
            }

        }

        private XmlDocument CreateOrderResponse(SampleResponse sampleResponse) {
            try {
                var doc = new XmlDocument();
                var SI = doc.CreateElement("SI");
                
                var BF = doc.CreateAttribute("BF");
                BF.Value = sampleResponse.SampleType;
                SI.Attributes.Append(BF);

                var WO = doc.CreateAttribute("WO");
                var orders = sampleResponse.TestOrders.Where(o => o.Code == "CHEM" || o.Code == "MICRO")
                                .Select(o => o.Code).ToList();
                if (orders.Contains("CHEM") && orders.Contains("MICRO")) {
                    WO.Value = "run";
                } else if (orders.Contains("CHEM")) {
                    WO.Value = "chem-only";
                } else if (orders.Contains("MICRO")) {
                    WO.Value = "micro-only";
                } else {
                    WO.Value = "skip";
                }
                SI.Attributes.Append(WO);

                SI.InnerText = sampleResponse.SampleID;

                doc.AppendChild(SI);

                return doc;
            } catch (Exception ex) {
                Logger.Error(ex, $"Create Order Response Failed with Reason ({ex.Message}) !");
                return null;
            }
        }

        private XmlDocument CreateUnknownResponse(SampleResponse sampleResponse) {
            try {
                var doc = new XmlDocument();
                var UNKID = doc.CreateElement("UNKID");
                UNKID.InnerText = sampleResponse.SampleID;
                doc.AppendChild(UNKID);
                return doc;
            } catch (Exception ex) {
                Logger.Error(ex, $"Create Unknown Response Failed with Reason ({ex.Message}) !");
                return null;
            }
        }
    }
}
