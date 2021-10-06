using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeviceLink.Structure
{
    public enum SampleDilution
    {
        None,
        Diluted,
        Concentrated
    }

    public class SampleInfo
    {
        public string SampleID { get; set; }
        public string SampleNo { get; set; }
        public string OldSampleNo { get; set; }
        public string RackNo { get; set; }
        public string CupPos { get; set; }
        public string SampleType { get; set; }
        public bool IsRerun { get; set; }

    }

    public class TestOrder
    {
        public string Code { get; set; }
        public SampleDilution Dilution { get; set; }
    }

    public class TestResult
    {
        public string Code { get; set; }
        public string Result { get; set; }
        public string Unit { get; set; }
        public string Flags { get; set; }
    }

    public abstract class PatientInfo
    {
        public string Name { get; set; }
        public string ChartNo { get; set; }
        public string Gender { get; set; }
        public DateTime Birthdate { get; set; }
    }

    public class SampleResponse : SampleInfo
    {
        public bool IsEmergency { get; set; }
        public IList<TestOrder> TestOrders { get; set; }
    }
    public class SampleResult : SampleInfo
    {
        public DateTime ReportDateTime { get; set; }
        public IList<TestResult> TestResults { get; set; }
    }
    public class QcResult : SampleInfo
    {
        public string ControlNo { get; set; }
        public DateTime ReportDateTime { get; set; }
        public IList<TestResult> QcResults { get; set; }
    }

    public class TubeInfo {
        public string SampleID { get; set; }
        public string SampleType { get; set; }
    }

    public class TubeOrder : TubeInfo {
        public bool IsEmergency { get; set; }
        public IList<TestOrder> TestOrders { get; set; }
    }

    public class TubeResult : TubeInfo {
        public int TubeType { get; set; }
        public int WorkplaceType { get; set; }
        public string ArchiveID { get; set; }
        public string RackID { get; set; }
        public string RowID { get; set; }
        public string ColumnID { get; set; }
        public DateTime ReportDateTime { get; set; }
        public int Volume { get; set; }
        public IList<TestOrder> TestOrders { get; set; }
    }
}
