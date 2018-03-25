using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Rainmeter;

using System.Diagnostics;
using System.Linq;
using System.Collections;
using System.Threading;

namespace ProcessMonitor
{
    //These are the possible default measure types
    //Default measure types have all info from Perfmance Monitor preconfigured for ease of use
    //@TODO Find a way to implement NETDOWN and NETUP
    //@TODO Add variants to some of theses
    public enum MeasureType
    {
        CPU,
        RAM,
        IO,
        GPU,
        VRAM,
        NETDOWN,
        NETUP,
        CUSTOM
    }
    public class MeasureCategory
    {
        public String Category { get; }
        public String SubCategory { get; }
        public MeasureType Type { get; }

        public MeasureCategory(MeasureType type)
        {
            this.Type = type;
            if (type == MeasureType.CPU)
            {
                Category = "Process";
                SubCategory = "% Processor Time";
            }
            else if (type == MeasureType.RAM)
            {
                Category = "Process";
                SubCategory = "Working Set";
            }
            else if (type == MeasureType.IO)
            {
                Category = "Process";
                SubCategory = "IO Data Bytes/sec";
            }
            else if (type == MeasureType.GPU)
            {
                Category = "GPU Engine";
                SubCategory = "Utilization Percentage";
            }
            else if (type == MeasureType.VRAM)
            {
                Category = "GPU Process Memory";
                SubCategory = "Dedicated Usage";
            }
        }
        public MeasureCategory(String category, String subCategory)
        {
            this.Category = category;
            this.SubCategory = subCategory;
            this.Type = MeasureType.CUSTOM;
        }
    }

    //Custom counter that implements IComparable so that it can be easily sorted
    public class Counter : IComparable<Counter>
    {

        public String Name;
        public float Value;
        public CounterSample Sample;

        public Counter(String Name, float Value, CounterSample Sample)
        {
            this.Name = Name;
            this.Value = Value;
            this.Sample = Sample;
        }

        public int CompareTo(Counter that)
        {
            if (this.Value > that.Value) return -1;
            if (this.Value == that.Value) return 0;
            return 1;
        }
    }

    //Counters is basically a glorified dictionary that contains all the info and threads for updating each counter
    //Each counter is exposed in a ReadOnly fashion and is locked from reading during the write (Which is very short since temps are used)
    //Counters aggragate what measures are also using it and are only allocated and updated when a measure is still using them
    public static class Counters
    {
        public class CounterLists
        {
            //Both a list sorted by usage and a dictionary are able to be used to access the processes 
            public Dictionary<String, Dictionary<String, Counter>> ByName;
            public Dictionary<String, List<Counter>> ByUsage;
            public Dictionary<string, double> _Sum;

            //This is a list of all the measures using the same category and what each ones update rate is (The lowest rate is the one used
            private Dictionary<String, int> updateRates;
            //This is a list of subcategories for this measure
            private Dictionary<String, String> subCategories;
            //This is the thread that will update its update rate dynamically to the lowest update rate a measure has set
            private Timer UpdateTimer;
            private int UpdateTimerRate;
            private Object UpdateTimerLock = new Object();

            //Function used to update counters
            private void UpdateCounters(String category, Dictionary<String, String> subCategories, bool isPID)
            {
                if (Monitor.TryEnter(UpdateTimerLock))
                {
                    try
                    {
                        var currCategory = new PerformanceCounterCategory(category).ReadCategory();
                        _Sum = new Dictionary<string, double>();

                        foreach (String subCategory in subCategories.Values)
                        {
                            var temp = currCategory[subCategory];

                            //@TODO replace _Sum key check with the options check once options object is done
                            if (temp != null && !_Sum.ContainsKey(subCategory))
                            {
                                Dictionary<String, Counter> tempByName = new Dictionary<String, Counter>(temp.Count);
                                List<Counter> tempByUsage = new List<Counter>(temp.Count);
                                _Sum.Add(subCategory, 0);

                                foreach (InstanceData instance in temp.Values)
                                {
                                    Counter counter = new Counter(instance.InstanceName, instance.RawValue, instance.Sample);
                                    //Counter name is a PID and needs to be converted to a process name
                                    if (isPID)
                                    {
                                        //"pid_12952_luid_0x00000000_0x00009AC6_phys_0_eng_0_engtype_3D"
                                        //"pid_11528_luid_0x00000000_0x0000A48E_phys_0"
                                        //This could be more hard coded but I wanted to be more versitile;
                                        int start = counter.Name.IndexOf("pid_") + "pid_".Length;
                                        int end = counter.Name.IndexOf("_", start);

                                        if (Int32.TryParse(counter.Name.Substring(start, end - start), out int myPid))
                                        {
                                            try
                                            {
                                                //PIDs will not be interpreted if there is no info to go on and will be left as is
                                                if (pids.Count > 0)
                                                {
                                                    counter.Name = pids[myPid];
                                                }
                                            }
                                            catch
                                            {
                                                API.Log((int)API.LogType.Debug, "Could not find a process with PID of " + myPid + " this PID will be ignored till found");
                                                continue;
                                            }
                                        }
                                    }

                                    if (this.ByName.ContainsKey(subCategory) && this.ByName[subCategory].ContainsKey(counter.Name))
                                    {
                                        //If last update or this update did not have a raw value then assume it is still 0
                                        if (this.ByName[subCategory][counter.Name].Sample.RawValue != 0 
                                            && counter.Sample.RawValue != 0)
                                        {
                                            counter = new Counter(counter.Name, CounterSample.Calculate(this.ByName[subCategory][counter.Name].Sample, counter.Sample), counter.Sample);
                                        }
                                        else
                                        {
                                            counter = new Counter(counter.Name, 0, counter.Sample);
                                        }
                                    }
                                    //@TODO decide if I want to have values possibly be wrong for one cycle or be 0 for one cycle
                                    else
                                    {
                                        counter = new Counter(counter.Name, 0, counter.Sample);
                                    }

                                    //Check if we already have a counter with the same name, if we do combine the two
                                    if (tempByName.ContainsKey(counter.Name))
                                    {
                                        //Yes this is a mess, this is mostly needed for GPU processes but will also be needed for process rollups
                                        //What it is for is if two counters exist with the same name we need to merge the final value for rainmeter
                                        //But we also need to merge the counter sample values as well so that way proper readable values can be translated next cycle
                                        tempByName[counter.Name].Value += counter.Value;
                                        tempByName[counter.Name].Sample = new CounterSample(tempByName[counter.Name].Sample.RawValue + counter.Sample.RawValue,
                                            tempByName[counter.Name].Sample.BaseValue, tempByName[counter.Name].Sample.CounterFrequency,
                                            tempByName[counter.Name].Sample.SystemFrequency, tempByName[counter.Name].Sample.TimeStamp,
                                            tempByName[counter.Name].Sample.TimeStamp100nSec, tempByName[counter.Name].Sample.CounterType);
                                    }
                                    else
                                    {
                                        tempByName.Add(counter.Name, counter);
                                    }

                                    //Custom sum variable so that special ones can be summed up without interfering with total
                                    if (counter.Name != "_Total")
                                    {
                                        _Sum[subCategory] += counter.Value;
                                    }
                                }
                                tempByUsage = tempByName.Values.ToList();
                                tempByUsage.Sort();

                                if (this.ByName.ContainsKey(subCategory))
                                {
                                    this.ByName[subCategory] = tempByName;
                                }
                                else
                                {
                                    this.ByName.Add(subCategory, tempByName);
                                }
                                if (this.ByName.ContainsKey(subCategory))
                                {
                                    this.ByUsage[subCategory] = tempByUsage;
                                }
                                else
                                {
                                    this.ByUsage.Add(subCategory, tempByUsage);
                                }
                            }
                            else
                            {
                                API.Log((int)API.LogType.Debug, "Could not find a performance counter in " + category + " called " + subCategory);
                            }
                        }
                    }
                    finally
                    {
                        Monitor.Exit(UpdateTimerLock);
                    }
                }
            }

            public CounterLists(String category, String subCategory, String ID, bool isPID, int updateInMS)
            {
                this.ByName = new Dictionary<String, Dictionary<String, Counter>>();
                this.ByUsage = new Dictionary<String, List<Counter>>();
                this.updateRates = new Dictionary<String, int> { { ID, updateInMS } };
                this.subCategories = new Dictionary<String, String> { { ID, subCategory } };

                if (isPID)
                {
                    pidIDs.Add(ID, updateInMS);
                    //@TODO This is still kinda slow, maybe going event based would be better

                    if (pidUpdateTimer == null || pidUpdateTimerRate > updateInMS)
                    {
                        pidUpdateTimerRate = updateInMS;
                        pidUpdateTimer = new Timer((stateInfo) => UpdatePIDs(), null, 0, updateInMS);
                    }
                }

                this.UpdateTimer = new Timer((stateInfo) => UpdateCounters(category, subCategories, isPID), null, 0, updateInMS);
                this.UpdateTimerRate = updateInMS;
            }

            //Add new ID to instance and check update timer needs to be decrease (Will also update rate if an instance already existed)
            public void AddInstance(String category, String subCategory, String ID, bool isPID, int updateInMS)
            {
                if (!this.updateRates.ContainsKey(ID))
                {
                    this.updateRates.Add(ID, updateInMS);

                    if (this.UpdateTimerRate > updateInMS)
                    {
                        this.UpdateTimer.Change(0, updateInMS);
                    }
                }
                else if(this.updateRates[ID] != updateInMS)
                    {
                    this.updateRates[ID] = updateInMS;

                    if (this.UpdateTimerRate > updateInMS)
                    {
                        if (this.UpdateTimer != null)
                        {
                            this.UpdateTimer.Change(0, updateInMS);
                        }
                        //Somehow timer got deintialized and we ended up here without it being reinitialized
                        else
                        {
                            this.UpdateTimer = new Timer((stateInfo) => UpdateCounters(category, subCategories, isPID), null, 0, updateInMS);
                        }
                    }
                }

                if (!this.subCategories.ContainsKey(ID))
                {
                    this.subCategories.Add(ID, subCategory);
                }
                else if (this.subCategories[ID] != subCategory)
                {
                    this.subCategories[ID] = subCategory;
                }
            }
            public void RemoveInstance(String ID)
            {
                if (this.updateRates.ContainsKey(ID) && this.updateRates[ID] == this.UpdateTimerRate)
                {
                    if (pidIDs.ContainsKey(ID))
                    {
                        if (pidIDs.Count == 1)
                        {
                            pidUpdateTimer.Dispose();
                            pidUpdateTimer = null;
                            pidUpdateTimerRate = int.MaxValue;
                        }
                        pidIDs.Remove(ID);
                    }
                    //There is more than one ID using this, find the new update rate
                    if (this.updateRates.Count > 1)
                    {
                        int min = int.MaxValue;

                        //Find smallest update time in list (Should be near O(1) in real world since no one should be changing this)
                        foreach (int interval in this.updateRates.Values)
                        {
                            if (interval < min)
                            {
                                min = interval;
                            }
                            if (min == UpdateTimerRate)
                            {
                                break;
                            }
                        }

                        this.UpdateTimerRate = min;
                        this.UpdateTimer.Change(0, min);
                    }
                    //Only one timer just remove it and disable thread
                    else
                    {
                        this.UpdateTimer.Dispose();
                        this.UpdateTimer = null;
                        this.UpdateTimerRate = int.MaxValue;
                    }
                }
                this.updateRates.Remove(ID);
            }

            public int Count()
            {
                return this.updateRates.Count();
            }
        }

        //This holds all the counters we are monitoring, when one stops being monitored it will be removed from the list
        //Each counter handles its own update
        public static Dictionary<String, CounterLists> counters = new Dictionary<String, CounterLists>(sizeof(MeasureType));

        //This is a list of all the PIDs and their associated process name, used to decode pids to process names
        private static Dictionary<int, String> pids = new Dictionary<int, String>();
        //Used to update pids, is set to lowest update rate of a counter that needs it
        private static Timer pidUpdateTimer;
        private static int pidUpdateTimerRate = int.MaxValue;
        private static Object pidUpdateLock = new Object();
        private static void UpdatePIDs()
        {
            if (Monitor.TryEnter(pidUpdateLock))
            {
                try
                {
                    var pidCounter = new PerformanceCounterCategory("Process").ReadCategory()["ID Process"];
                    var temp = new Dictionary<int, String>(pidCounter.Count);

                    foreach (InstanceData pid in pidCounter.Values)
                    {
                        try
                        {
                            //Both Idle and _Total share a PID, ignore them
                            if (pid.RawValue != 0)
                            {
                                temp.Add((int)pid.RawValue, pid.InstanceName);
                            }
                        }
                        catch
                        {
                            //PIDs should be unique but if they somehow are not throw an error
                            API.Log((int)API.LogType.Debug, "Found another process with the pid of" + pid);
                        }
                    }
                    pids = temp;
                }
                finally
                {
                    Monitor.Exit(pidUpdateLock);
                }
            }
        }
        //List of all measures 
        private static Dictionary<String, int> pidIDs = new Dictionary<String, int>();


        //Adds a new counter
        public static void AddCounter(String category, String subCategory, String ID, bool isPID = false, int updateInMS = 1000)
        {
            //If it already exists just add the ID and update rate to the list
            if (counters.TryGetValue(category, out CounterLists counter))
            {
                counter.AddInstance(category, subCategory, ID, isPID, updateInMS);
            }
            //If counter does not yet exist it will need to be created
            else
            {
                CounterLists newCounter = new CounterLists(category, subCategory, ID, isPID, updateInMS);
                counters.Add(category, newCounter);
            }

        }
        public static void RemoveCounter(String category, String subCategory, String ID)
        {
            //If counter exists remove ID from it
            if (counters.TryGetValue(category, out CounterLists counter))
            {
                counter.RemoveInstance(ID);
                //If nothing is referencing that counter anymore remove and deallocate it
                if (counter.Count() == 0)
                {
                    counter = null;
                    counters.Remove(category);
                }
            }
        }
        public static bool GetCounterLists(String category, String subCategory, out CounterLists counterLists)
        {
            return Counters.counters.TryGetValue(category, out counterLists);
        }
    }

    class Measure
    {
        static public implicit operator Measure(IntPtr data)
        {
            return (Measure)GCHandle.FromIntPtr(data).Target;
        }
        public Rainmeter.API API;

        public MeasureCategory myCategories;
        public int myInstance;
        public String myName;

        public bool isRollup;
        public bool isPercent;
    }

    public class Plugin
    {
        [DllExport]
        public static void Initialize(ref IntPtr data, IntPtr rm)
        {
            data = GCHandle.ToIntPtr(GCHandle.Alloc(new Measure()));
            ((Measure)data).API = (Rainmeter.API)rm;
        }

        [DllExport]
        public static void Finalize(IntPtr data)
        {
            Measure measure = (Measure)data;

            Counters.RemoveCounter(measure.myCategories.Category, measure.myCategories.SubCategory, measure.API.GetSkin() + measure.API.GetMeasureName());

            GCHandle.FromIntPtr(data).Free();
        }

        [DllExport]
        public static void Reload(IntPtr data, IntPtr rm, ref double maxValue)
        {
            Measure measure = (Measure)data;
            measure.API = (Rainmeter.API)rm;

            bool isPID = false;

            try
            {
                MeasureType type = (MeasureType)Enum.Parse(typeof(MeasureType), measure.API.ReadString("Type", "CPU"), true);

                //Check if type is custom if it is not just set category, if it is then read custom options
                if (type != MeasureType.CUSTOM)
                {
                    measure.myCategories = new MeasureCategory(type);

                    if (type == MeasureType.GPU || type == MeasureType.VRAM)
                    {
                        isPID = true;
                    }

                }
                else
                {
                    measure.myCategories = new MeasureCategory(
                        measure.API.ReadString("Category", "Process"),
                        measure.API.ReadString("SubCategory", "% Processor Time"));
                }

                measure.isRollup = measure.API.ReadInt("Rollup", 0) != 0;
                //Is precent is on by default when measure type is CPU
                measure.isPercent = measure.API.ReadInt("Percent", type == MeasureType.CPU ? 1 : 0) != 0;
            }
            catch
            {
                measure.API.Log(API.LogType.Error, "Type=" + measure.API.ReadString("Type", "CPU") + " was not in the list of predefined categories, assuming CPU");
                measure.myCategories = new MeasureCategory(MeasureType.CPU);
            }

            Counters.AddCounter(measure.myCategories.Category, measure.myCategories.SubCategory, measure.API.GetSkin() + measure.API.GetMeasureName(), isPID);

            measure.myInstance = measure.API.ReadInt("Instance", -1);
            measure.myName = measure.API.ReadString("Name", null);
        }

        [DllExport]
        public static double Update(IntPtr data)
        {
            Measure measure = (Measure)data;
            double ret = 0;
            
            if (Counters.GetCounterLists(measure.myCategories.Category, measure.myCategories.SubCategory, out Counters.CounterLists counters))
            {
                if (counters.ByUsage.Count > measure.myInstance && measure.myInstance >= 0 && counters.ByUsage.ContainsKey(measure.myCategories.SubCategory))
                {
                    ret = counters.ByUsage[measure.myCategories.SubCategory][measure.myInstance].Value;
                }
                else if (measure.myName.Length > 0 && counters.ByUsage.ContainsKey(measure.myCategories.SubCategory) && counters.ByName[measure.myCategories.SubCategory].Count > 0)
                {
                    if(measure.myName == "_Sum")
                    {
                        ret = counters._Sum[measure.myCategories.SubCategory];
                    }
                    else if (counters.ByName[measure.myCategories.SubCategory].TryGetValue(measure.myName, out Counter counter))
                    {
                        ret = counter.Value;
                    }
                    else
                    {
                        measure.API.Log(API.LogType.Debug, "Could not find a counter with the name " + measure.myName);
                    }
                }

                if(measure.isPercent && counters.ByUsage.ContainsKey(measure.myCategories.SubCategory))
                {
                    if(counters.ByName[measure.myCategories.SubCategory].TryGetValue("_Total", out Counter counter) && counter.Value > 0)
                    {
                        ret = (ret / counter.Value) * 100;
                    }
                    else if (counters._Sum.ContainsKey(measure.myCategories.SubCategory) && counters._Sum[measure.myCategories.SubCategory] > 0)
                    {
                        ret = (ret / counters._Sum[measure.myCategories.SubCategory]) * 100;
                    }
                }
            }
            return ret;
        }

        [DllExport]
        public static IntPtr GetString(IntPtr data)
        {
            Measure measure = (Measure)data;

            if (Counters.GetCounterLists(measure.myCategories.Category, measure.myCategories.SubCategory, out Counters.CounterLists counters))
            {
                if (counters.ByUsage.Count > measure.myInstance && measure.myInstance >= 0 && counters.ByUsage.ContainsKey(measure.myCategories.SubCategory))
                {
                    return Marshal.StringToHGlobalUni(counters.ByUsage[measure.myCategories.SubCategory][measure.myInstance].Name);
                }
                else if (counters.ByName.Count > 0 && measure.myName.Length > 0 && counters.ByUsage.ContainsKey(measure.myCategories.SubCategory))
                {
                    //@TODO Should we maybe just always return the name?
                    if (measure.myName == "_Sum")
                    {
                        return Marshal.StringToHGlobalUni("_Sum");
                    }
                    else if (counters.ByName[measure.myCategories.SubCategory].TryGetValue(measure.myName, out Counter counter))
                    {
                        return Marshal.StringToHGlobalUni(counter.Name);
                    }
                    else
                    {
                        measure.API.Log(API.LogType.Debug, "Could not find a counter with the name " + measure.myName);
                    }
                }
            }
            return IntPtr.Zero;
        }

        //[DllExport]
        //public static void ExecuteBang(IntPtr data, [MarshalAs(UnmanagedType.LPWStr)]String args)
        //{
        //    Measure measure = (Measure)data;
        //}

        //[DllExport]
        //public static IntPtr (IntPtr data, int argc,
        //    [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] String[] argv)
        //{
        //    Measure measure = (Measure)data;
        //
        //    return Marshal.StringToHGlobalUni(""); //returning IntPtr.Zero will result in it not being used
        //}
    }
}
