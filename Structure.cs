﻿using System;
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
        public IList<TestOrder> TestOrders { get; set; }
    }
    public class SampleResult : SampleInfo
    {
        public IList<TestResult> TestResults { get; set; }
    }
    public class QcResult : SampleInfo
    {
        public string ControlNo { get; set; }
        public IList<TestResult> QcResults { get; set; }
    }


}
