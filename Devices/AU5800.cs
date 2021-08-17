using DeviceLink.Const;
using DeviceLink.Extensions;
using DeviceLink.Interface;
using DeviceLink.Structure;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using Utility.Extensions.Enumerable;

namespace DeviceLink.Devices
{
    public class AU5800 : SerialDevice, IDevice
    {
        private string mDeviceNo;
        private string mComPort;
        private IDeviceListener mListener;
        
        private bool mReceiveETBETX = false;
        private List<char> mUpBuffer = new List<char>() { };
        private List<char> mDnBuffer = new List<char>() { };
        private int mRetryCount = 0;


        private int mExpectBlockNo = 0;
        private SampleResponse mSampleResponse = null;
        private SampleResult mSampleResult = null;
        private QcResult mQcResult = null;

        private enum SampleRequestResult
        {
            Result_Found,
            Result_NotFound,
            Result_NoTest
        }

        private enum SampleResultStatus
        {
            Result_Incorrect,
            Result_InComplete,
            Result_Complete
        }

        private const int RETRY_LIMIT = 3;

        public AU5800(string DeviceNo, string ComPort, IDeviceListener Listener) : base(ComPort, 9600, Parity.None, 8, StopBits.One) {
            
            mDeviceNo = DeviceNo;
            mComPort = ComPort;
            mListener = Listener;
            Logger = NLog.LogManager.GetLogger($"DeviceLink.AU5800_{mDeviceNo}");
        }

        public override void Start()
        {
            base.Start();
            ChangeState(DeviceState.Idle);
        }

        public override void Stop()
        {
            base.Stop();
            ChangeState(DeviceState.Disconnect);
        }

        protected override void ReadData(char ChrData)
        {
            mListener.OnDataReceiving(mDeviceNo, ChrData.ToPrintOutString());
            switch(State)
            {
                case DeviceState.Idle:
                    switch(ChrData)
                    {
                        case (char)ControlCode.STX:
                            Logger.Info($"Receive [STX]");
                            mReceiveETBETX = false;
                            mUpBuffer.Clear();
                            mDnBuffer.Clear();
                            ChangeState(DeviceState.Upload);
                            break;
                        default:
                            break;
                    }
                    break;
                case DeviceState.Upload:
                    if (mReceiveETBETX == false)
                    {
                        switch (ChrData)
                        {
                            case (char)ControlCode.ETB:
                                Logger.Info($"Receive [ETB]");
                                mReceiveETBETX = true;
                                mUpBuffer.Add(ChrData);
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
                    else
                    {
                        var chrCheckSum = ChrData;
                        Logger.Info($"Recieve Check Sum {BitConverter.ToString(new byte[] { (byte)ChrData })}");
                        var frameChecked = CheckFrame(mUpBuffer, chrCheckSum);
                        if (frameChecked)
                        {
                            Logger.Info($"Frame Check Success");
                            WriteData((char)ControlCode.ACK);
                            ProcessData(new string(mUpBuffer.ToArray()));

                        }
                        else
                        {
                            WriteData((char)ControlCode.NAK);
                        }
                        //ChangeState(DeviceState.Idle);
                    }
                    break;
                case DeviceState.Download:
                    switch(ChrData)
                    {
                        case (char)ControlCode.ACK:
                            Logger.Info($"Receive [ACK] in Device State {State.ToString("G")}");
                            if (State == DeviceState.Download)
                            {
                                ChangeState(DeviceState.Idle);
                                mListener.OnSampleResponseAcknowledged(mDeviceNo, mSampleResponse);
                                mSampleResponse = null;
                            }
                            break;
                        case (char)ControlCode.NAK:
                            Logger.Info($"Receive [NAK] in Device State {State.ToString("G")}");
                            if (State == DeviceState.Download)
                            {
                                RetryWriteData();
                            }
                            break;
                        default:
                            break;
                    }
                    break;
                default:
                    break;
            }
        }

        protected override void ChangeState(DeviceState state)
        {
            base.ChangeState(state);
            mListener.OnStateChanging(mDeviceNo, State);
        }

        protected override void WriteData(string Data)
        {
            mDnBuffer = Data.ToCharArray().ToList();
            base.WriteData(Data);
            mListener.OnDataWriting(mDeviceNo, Data.ToPrintOutString());
        }

        private void RetryWriteData()
        {
            if (mRetryCount < RETRY_LIMIT)
            {
                var message = new string(mDnBuffer.ToArray());
                Logger.Info($"Retry {(mRetryCount + 1).ToString("D")} time of Writing Data <{message.ToPrintOutString()}>");
                WriteData(message);
                mRetryCount++;
            } else
            {
                Logger.Info($"Exceeding the Retry Limit {RETRY_LIMIT.ToString("D")} times");
                ChangeState(DeviceState.Idle);
            }
        }
        protected override void WriteData(char Data)
        {
            base.WriteData(Data);
            mListener.OnDataWriting(mDeviceNo, Data.ToPrintOutString());
        }

        private bool CheckFrame(List<char> Data, char Checksum)
        {
            var calCheckSum = ComputeXOrChecksum(Data);
            if (Checksum != calCheckSum) {
                Logger.Info($"Check Fram Fail with Reason with Check Sum mis-matched (Calculated Check Sum = {BitConverter.ToString(new byte[] { (byte)calCheckSum })}, Expected Check Sum = {BitConverter.ToString(new byte[] { (byte)Checksum })}");
                return false;
            }
            //Todo: add Block No checking for TestResult, QcResult, RerunResult
            var strData = new string(Data.ToArray());
            var frameNo = strData.Substring(0, 2);
            if ("D ,d ,DH,dH,DQ".Contains(frameNo))
            {
                var blockNo = strData.Substring(29, 1);
                var msgEnd = strData.Last();
                switch (msgEnd)
                {
                    case (char)ControlCode.ETB:
                        if (blockNo != mExpectBlockNo.ToString("D")) {
                            Logger.Info($"Check Frame Fail with Reason Block No. mis-matched (Expect Block No. = {mExpectBlockNo.ToString("D")}, Actual Block No. = {blockNo})");
                            return false;
                        }
                        mExpectBlockNo++;
                        break;
                    case (char)ControlCode.ETX:
                        if (blockNo != "E") {
                            Logger.Info($"Check Frame Fail with Reason Block No. mis-matched (Expect Block No. = E, Actual Block No. = {blockNo}");
                            return false;
                        }
                        mExpectBlockNo = 0;
                        break;
                    default:
                        Logger.Info($"Check Frame Fail with Reason (Unexpected Message End = {msgEnd.ToPrintOutString()})");
                        return false;
                }
            }
            return true;
        }

        private void ProcessData(string Data)
        {
            Logger.Info($"Process Data <{Data.ToPrintOutString()}>");
            string msgCode = Data.Substring(0, 2);
            switch(msgCode)
            {
                case "R ":
                case "RH":
                case "Rh":
                    ChangeState(DeviceState.Download);
                    var requestResult = ProcessSampleRequest(Data, ref mSampleResponse);
                    switch(requestResult)
                    {
                        case SampleRequestResult.Result_NotFound:
                            WriteNoSample();
                            break;
                        case SampleRequestResult.Result_NoTest:
                            WriteNoTestSample(mSampleResponse);
                            break;
                        case SampleRequestResult.Result_Found:
                            WriteTestSample(mSampleResponse);
                            break;
                    }
                    break;
                //case "RE":
                //    ChangeState(DeviceState.Download);
                //    WriteNoSample();
                //    break;
                case "D ":
                case "d ":
                case "DH":
                case "dH":
                    var resultStatus = ProcessSampleResult(Data, ref mSampleResult);
                    switch(resultStatus)
                    {
                        case SampleResultStatus.Result_Incorrect:
                            Logger.Error($"Process Sample Result Fail with Reason Frame Structure mis-matched (FrameNo = {msgCode}, Data = <{Data.ToPrintOutString()}>)");
                            mSampleResult = null;
                            ChangeState(DeviceState.Idle);
                            break;
                        case SampleResultStatus.Result_InComplete:
                            Logger.Info($"Process Sample Result and Waitting for Next Block (Data = <{Data.ToPrintOutString()}>)");
                            // No action, wait for next result block received
                            break;
                        case SampleResultStatus.Result_Complete:
                            Logger.Info($"Sample Result is {JsonConvert.SerializeObject(mSampleResult)}");
                            mListener.OnSampleResultReceived(mDeviceNo, mSampleResult);
                            mSampleResult = null;
                            ChangeState(DeviceState.Idle);
                            break;
                    }
                    break;
                case "DQ":
                    var qcResultStatus = ProcessQcResult(Data, ref mQcResult);
                    switch(qcResultStatus)
                    {
                        case SampleResultStatus.Result_Incorrect:
                            Logger.Error($"Process Qc Result Fail with Reason Frame Structure mis-matched (FrameNo = {msgCode}, Data = <{Data.ToPrintOutString()}>)");
                            mQcResult = null;
                            ChangeState(DeviceState.Idle);
                            break;
                        case SampleResultStatus.Result_InComplete:
                            Logger.Info($"Process Qc Result and Waitting for Next Block (Data = <{Data.ToPrintOutString()}>)");
                            // No action, wait for next QC result block received
                            break;
                        case SampleResultStatus.Result_Complete:
                            Logger.Info($"Qc Result is {JsonConvert.SerializeObject(mQcResult)}");
                            mListener.OnQcResultReceived(mDeviceNo, mQcResult);
                            mQcResult = null;
                            ChangeState(DeviceState.Idle);
                            break;
                    }
                    break;
                default:
                    ChangeState(DeviceState.Idle);
                    break;
            }
        }

        private SampleRequestResult ProcessSampleRequest(string requestData, ref SampleResponse responseSample)
        {
            var requestSample = new SampleInfo
            {
                RackNo = requestData.Substring(4, 4),
                CupPos = requestData.Substring(8, 2),
                SampleType = requestData.Substring(10, 1),
                SampleNo = requestData.Substring(11, 4),
                SampleID = requestData.Substring(15, 10),
                OldSampleNo = ""
            };
            responseSample = mListener.OnSampleRequestReceived(mDeviceNo, requestSample);
            if (responseSample is null) { return SampleRequestResult.Result_NotFound; }
            if (responseSample.TestOrders.IsNullOrEmpty()) { return SampleRequestResult.Result_NoTest; }
            if (responseSample.SampleType == "W") { responseSample.SampleType = " "; }
            return SampleRequestResult.Result_Found;
        }

        private void WriteNoSample()
        {
            var message = GenerateWriteMessage("SE");
            WriteData(message);
        }

        private void WriteNoTestSample(SampleResponse response)
        {
            var fs = new Func<SampleResponse, string>((s) =>
            {
                return "S " + s.RackNo.ToFixLengthString(4) + s.CupPos.ToFixLengthString(2) +
                    s.SampleType.ToFixLengthString(1) + s.SampleNo.ToFixLengthString(4) + 
                    s.SampleID.ToFixLengthString(10) + " ".Repeat(4) + "E";
            });
            var data = fs(response);
            var message = GenerateWriteMessage(data);
            WriteData(message);
        }

        private void WriteTestSample(SampleResponse response)
        {
            var fs = new Func<SampleResponse, string>((s) => {
                var result = "S " + s.RackNo.ToFixLengthString(4) + s.CupPos.ToFixLengthString(2) +
                    s.SampleType.ToFixLengthString(1) + s.SampleNo.ToFixLengthString(4) +
                    s.SampleID.ToFixLengthString(10) + " ".Repeat(4) + "E";
                if (response.TestOrders.IsNullOrEmpty()) { return result; }
                foreach (var order in response.TestOrders)
                {
                    result += order.Code.ToFixLengthString(3);
                    switch(order.Dilution)
                    {
                        case SampleDilution.None:
                            result += "0";
                            break;
                        case SampleDilution.Diluted:
                            result += "1";
                            break;
                        case SampleDilution.Concentrated:
                            result += "2";
                            break;
                    }
                }
                return result;
            });
            var data = fs(response);
            var message = GenerateWriteMessage(data);
            WriteData(message);
        }

        private string GenerateWriteMessage(string Data)
        {
            var chrData = new List<char>();
            var chkData = new List<char>();

            chkData.AddRange(Data.ToCharArray());
            chkData.Add((char)ControlCode.ETX);
            var chrChkSum = ComputeXOrChecksum(chkData);

            chrData.Add((char)ControlCode.STX);
            chrData.AddRange(Data.ToCharArray());
            chrData.Add((char)ControlCode.ETX);
            chrData.Add(chrChkSum);

            return new string(chrData.ToArray());
        }

        private SampleResultStatus ProcessSampleResult(string resultData, ref SampleResult sampleResult)
        {
            try
            {
                var msgCode = resultData.Substring(0, 2);
                if (sampleResult is null)
                {
                    sampleResult = new SampleResult {
                        RackNo = resultData.Substring(4, 4),
                        CupPos = resultData.Substring(8, 2),
                        SampleType = resultData.Substring(10, 1),
                        SampleNo = resultData.Substring(11, 4),
                        SampleID = resultData.Substring(15, 10),
                        IsRerun = ("dH,DH".Contains(msgCode) ? true : false),
                        ReportDateTime = DateTime.Now,
                        TestResults = new List<TestResult>()
                    };
                }
                if (sampleResult.SampleType.Trim() == "") { sampleResult.SampleType = "W"; }
                var blockNo = resultData.Substring(29, 1);
                var stringTests = resultData.Substring(30, resultData.Length - 30 - 1);
                var tests = stringTests.Split(21).ToList();
                foreach (var test in tests)
                {
                    sampleResult.TestResults.Add(new TestResult
                    {
                        Code = test.Substring(1, 3),
                        Result = test.Substring(4, 9),
                        Flags = string.Join("^", test.Substring(13, 8).Split(2).ToArray())
                    });
                }
                var msgEnd = resultData.Last();
                if (blockNo == "E" && msgEnd == (char)ControlCode.ETX) { return SampleResultStatus.Result_Complete; }
                if ("0123456789".Contains(blockNo) || msgEnd == (char)ControlCode.ETB) { return SampleResultStatus.Result_InComplete; }
                return SampleResultStatus.Result_Incorrect;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Process Sample Result Fail Unexpectedly");
                return SampleResultStatus.Result_Incorrect;
            }
        }

        private SampleResultStatus ProcessQcResult(string resultData, ref QcResult qcResult)
        {
            try
            {
                if (qcResult is null)
                {
                    qcResult = new QcResult
                    {
                        RackNo = resultData.Substring(4, 4),
                        CupPos = resultData.Substring(8, 2),
                        SampleType = resultData.Substring(10, 1),
                        SampleNo = resultData.Substring(11, 4),
                        SampleID = resultData.Substring(15, 10).Trim(),
                        ControlNo = resultData.Substring(26, 3),
                        IsRerun = false,
                        ReportDateTime = DateTime.Now,
                        QcResults = new List<TestResult>()
                    };
                }
                var blockNo = resultData.Substring(29, 1);
                var stringTests = resultData.Substring(32, resultData.Length - 32 - 1);
                var tests = stringTests.Split(20).ToList();
                foreach (var test in tests)
                {
                    qcResult.QcResults.Add(new TestResult
                    {
                        Unit = resultData.Substring(30, 1),
                        Code = test.Substring(0, 3),
                        Result = test.Substring(3, 9),
                        Flags = string.Join("^", test.Substring(12, 8).Split(2).ToArray())
                    });
                }
                var msgEnd = resultData.Last();
                if (blockNo == "E" && msgEnd == (char)ControlCode.ETX) { return SampleResultStatus.Result_Complete; }
                if ("0123456789".Contains(blockNo) || msgEnd == (char)ControlCode.ETB) { return SampleResultStatus.Result_InComplete; }
                return SampleResultStatus.Result_Incorrect;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Process Qc Result Fail Unexpectedly");
                return SampleResultStatus.Result_Incorrect;
            }
        }
    }
}
