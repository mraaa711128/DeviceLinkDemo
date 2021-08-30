using DeviceLink.Interface;
using DeviceLink.Structure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Utility.Extensions;
using Utility.Extensions.Enumerable;

namespace DeviceLink.Devices {
    public class DxH800 : AstmSerialDevice, IDevice {

        public DxH800(string DeviceNo, string ComPort, IDeviceListener Listener) : base(DeviceNo, ComPort, Listener) {
            Logger = NLog.LogManager.GetLogger($"DeviceLink.DxH800_{DeviceNo}");
            RetryLimit = 6;
        }

        protected override bool ProcessRequestInfo(string frame, out string sampleID) {
            sampleID = null;
            try {
                var fields = frame.Split("|");
                sampleID = fields[2].Replace("!", "").Trim();
                return true;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process Request Info Fail with reason = {ex.Message}");
                return false;
            }
        }

        protected override bool ProcessOrderInfo(string frame, out bool isQcResult) {
            isQcResult = false;
            try {
                var fields = frame.Split("|");
                isQcResult = fields[11].Trim().Equals("Q");
                return true;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process Order Info Fail with reason = {ex.Message}");
                return false;
            }
        }

        protected override bool ProcessOrderInfoForQcResult(string frame, out QcResult qcResult) {
            qcResult = null;
            try {
                var fields = frame.Split("|");
                var values = fields[2].Split("!");
                qcResult = new QcResult {
                    SampleID = values[0].Trim(),
                    ReportDateTime = DateTime.MinValue,
                    QcResults = new List<TestResult>()
                };
                return true;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process Order Info for Qc Result Fail with reason = {ex.Message}");
                return false;
            }
        }

        protected override bool ProcessOrderInfoForSampleResult(string frame, out SampleResult sampleResult) {
            sampleResult = null;
            try {
                var fields = frame.Split("|");
                var date = fields[22].Substring(0, 8);
                var time = fields[22].Substring(8, 6);
                var datetime = new string[] { date, time }.ToAcDateTime();
                sampleResult = new SampleResult {
                    SampleID = fields[2].Trim(),
                    ReportDateTime = datetime
                };
                return true;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process Order Info for Sample Result Fail with reason = {ex.Message}");
                return false;
            }
        }

        protected override bool ProcessResultInfo(string frame, out DateTime reportDateTime, out TestResult outputData) {
            reportDateTime = DateTime.MinValue;
            outputData = null;
            try {
                var fields = frame.Split("|");
                if (fields[9].Trim().Equals("F") ||
                        fields[9].Trim().Equals("R")) {
                    var testIds = fields[2].Trim().Split("!");
                    var testValues = fields[3].Trim().Split("!");
                    outputData = new TestResult {
                        Code = testIds[3].Trim(),
                        Result = testValues[0].Trim(),
                        Flags = (testValues.Length >= 2 ? testValues[1].Trim() : null),
                        Unit = fields[4].Trim()
                    };
                    return true;
                }
                return false;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process Result Info Fail with reason = {ex.Message}");
                return false;
            }
        }

        protected override bool ProcessDownloadHeader(SampleResponse sampleResponse, out string headerFrame) {
            headerFrame = null;
            try {
                headerFrame = $"H|\\!~|||LIS|||||||P|1|{DateTime.Now.ToString("yyyyMMddHHmmss")}";
                return true;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process Download Header Fail with reason = {ex.Message}");
                return false;
            }
        }

        protected override bool ProcessDownloadPatient(SampleResponse sampleResponse, out string patientFrame) {
            patientFrame = null;
            try {
                patientFrame = "P|1|";
                return true;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process Download Patient Fail with reason = {ex.Message}");
                return false;
            }
        }

        protected override bool ProcessDownloadOrder(SampleResponse sampleResponse, out string orderFrame) {
            orderFrame = null;
            try {
                if (sampleResponse.TestOrders.IsNullOrEmpty()) {
                    orderFrame = $"O|1|{sampleResponse.SampleID}||!!!CD|R||||||||||Whole blood";
                } else {
                    orderFrame = $"O|1|{sampleResponse.SampleID}||";
                    var tests = sampleResponse.TestOrders.Select(x => x.Code).ToList();
                    if (tests.Count == 2 && tests.TrueForAll(t => "CBC,DC".Contains(t))) {
                        orderFrame += "!!!CD";
                    } else if (tests.Count == 1 && tests.Contains("CBC")) {
                        orderFrame += $"!!!CBC";
                    } else {
                        orderFrame += "!!!";
                    }
                    orderFrame += $"|{ (sampleResponse.IsEmergency ? "S" : "R")}||||||||||Whole blood";
                }
                return true;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process Download Order Fail with reason = {ex.Message}");
                return false;
            }
        }

        protected override bool ProcessDownloadTerminate(SampleResponse sampleResponse, out string terminateFrame) {
            terminateFrame = null;
            try {
                terminateFrame = "L|1|F";
                return true;
            } catch (Exception ex) {
                Logger.Error(ex, $"Process Download Terminate Fail with reason = {ex.Message}");
                return false;
            }
        }

    }
}
