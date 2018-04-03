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
    public enum MeasureAlias
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
    //Contains all the options for the measure and help funcitons for checking how much info other measures can share
    public class MeasureOptions
    {
        static public implicit operator MeasureOptions(IntPtr data)
        {
            return (MeasureOptions)GCHandle.FromIntPtr(data).Target;
        }
        public Rainmeter.API API;
        //One of these will normally be null
        public int Instance;
        public String Name;

        //Predefined options
        public bool IsPercent = false;
        public bool IsPID = false;
        public bool IsRollup = true;
        public int BlockType = 1;
        public List<String> BlockList = new List<string> { "_Total" };
        public int UpdateInMS = 1000;

        public string ID;
        public String Object;
        public String Counter;
        
        public void DeAlias(MeasureAlias alias)
        {
            if (alias == MeasureAlias.CPU)
            {
                this.Object = "Process";
                this.Counter = "% Processor Time";
                this.IsPercent = true;
            }
            else if (alias == MeasureAlias.RAM)
            {
                this.Object = "Process";
                this.Counter = "Working Set";
            }
            else if (alias == MeasureAlias.IO)
            {
                this.Object = "Process";
                this.Counter = "IO Data Bytes/sec";
            }
            else if (alias == MeasureAlias.GPU)
            {
                this.Object = "GPU Engine";
                this.Counter = "Utilization Percentage";
                this.IsPID = true;
            }
            else if (alias == MeasureAlias.VRAM)
            {
                this.Object = "GPU Process Memory";
                this.Counter = "Dedicated Usage";
                this.IsPID = true;
            }
        }
    }

    //Custom instance that implements IComparable so that it can be easily sorted
    public class Instance : IComparable<Instance>
    {

        public String Name;
        public float Value;
        public CounterSample Sample;

        public Instance(String Name, float Value, CounterSample Sample)
        {
            this.Name = Name;
            this.Value = Value;
            this.Sample = Sample;
        }

        public int CompareTo(Instance that)
        {
            if (this.Value > that.Value) return -1;
            if (this.Value == that.Value) return 0;
            return 1;
        }
    }

    //Instances is basically a glorified dictionary that contains all the info and threads for updating each instance
    //Each instance is exposed in a ReadOnly fashion and is locked from reading during the write (Which is very short since temps are used)
    //Instances aggragate what measures are also using it and are only allocated and updated when a measure is still using them
    //@TODO Rename a bunch of stuff in this class by hand
    public static class Instances
    {
        public class InstanceLists
        {
            //Both a list sorted by usage and a dictionary are able to be used to access the processes 
            public Dictionary<String, Dictionary<String, Instance>> ByName;
            public Dictionary<String, List<Instance>> ByUsage;
            public Dictionary<String, double> _Sum;

            //@TODO updateRates and counters can probably be merged to use new measureOptions object
            //@TODO check this entire class for possible redundancies that can be removed
            //This is a list of all the measures using the same object and what each ones update rate is (The lowest rate is the one used
            private Dictionary<String, int> updateRates;
            //This is a list of subcategories for this object
            private Dictionary<String, String> counters;
            //This is the thread that will update its update rate dynamically to the lowest update rate a measure has set
            private Timer UpdateTimer;
            private int UpdateTimerRate;
            private Object UpdateTimerLock = new Object();

            //Function used to update instances
            private void UpdateInstances(MeasureOptions options)
            {
                if (Monitor.TryEnter(UpdateTimerLock))
                {
                    try
                    {
                        //@TODO check if anything more can be done to this to reduce CPU usage
                        var currObject = new PerformanceCounterCategory(options.Object).ReadCategory();
                        _Sum = new Dictionary<String, double>();

                        foreach (String counter in counters.Values)
                        {
                            var temp = currObject[counter];

                            //@TODO replace _Sum key check with the options check once options object is done
                            if (temp != null && !_Sum.ContainsKey(counter))
                            {
                                Dictionary<String, Instance> tempByName = new Dictionary<String, Instance>(temp.Count);
                                List<Instance> tempByUsage = new List<Instance>(temp.Count);
                                _Sum.Add(counter, 0);

                                foreach (InstanceData instanceData in temp.Values)
                                {
                                    Instance instance = new Instance(instanceData.InstanceName, instanceData.RawValue, instanceData.Sample);
                                    //Instance name is a PID and needs to be converted to a process name
                                    if (options.IsPID)
                                    {
                                        //"pid_12952_luid_0x00000000_0x00009AC6_phys_0_eng_0_engtype_3D"
                                        //"pid_11528_luid_0x00000000_0x0000A48E_phys_0"
                                        //This could be more hard coded but I wanted to be more versitile;
                                        int start = instance.Name.IndexOf("pid_") + "pid_".Length;
                                        int end = instance.Name.IndexOf("_", start);

                                        if (Int32.TryParse(instance.Name.Substring(start, end - start), out int myPid))
                                        {
                                            try
                                            {
                                                //PIDs will not be interpreted if there is no info to go on and will be left as is
                                                if (pids.Count > 0)
                                                {
                                                    instance.Name = pids[myPid];
                                                }
                                            }
                                            catch
                                            {
                                                API.Log((int)API.LogType.Debug, "Could not find a process with PID of " + myPid + " this PID will be ignored till found");
                                                continue;
                                            }
                                        }
                                    }

                                    if (this.ByName.ContainsKey(counter) && this.ByName[counter].ContainsKey(instance.Name))
                                    {
                                        //If last update or this update did not have a raw value then assume it is still 0
                                        if (this.ByName[counter][instance.Name].Sample.RawValue != 0 
                                            && instance.Sample.RawValue != 0)
                                        {
                                            instance = new Instance(instance.Name, CounterSample.Calculate(this.ByName[counter][instance.Name].Sample, instance.Sample), instance.Sample);
                                        }
                                        else
                                        {
                                            instance = new Instance(instance.Name, 0, instance.Sample);
                                        }
                                    }
                                    //@TODO decide if I want to have values possibly be wrong for one cycle or be 0 for one cycle
                                    else
                                    {
                                        instance = new Instance(instance.Name, 0, instance.Sample);
                                    }

                                    //Check if we already have a instance with the same name, if we do combine the two
                                    if (tempByName.ContainsKey(instance.Name))
                                    {
                                        //Yes this is a mess, this is mostly needed for GPU processes but will also be needed for process rollups
                                        //What it is for is if two instances exist with the same name we need to merge the final value for rainmeter
                                        //But we also need to merge the instance sample values as well so that way proper readable values can be translated next cycle
                                        tempByName[instance.Name].Value += instance.Value;
                                        tempByName[instance.Name].Sample = new CounterSample(tempByName[instance.Name].Sample.RawValue + instance.Sample.RawValue,
                                            tempByName[instance.Name].Sample.BaseValue, tempByName[instance.Name].Sample.CounterFrequency,
                                            tempByName[instance.Name].Sample.SystemFrequency, tempByName[instance.Name].Sample.TimeStamp,
                                            tempByName[instance.Name].Sample.TimeStamp100nSec, tempByName[instance.Name].Sample.CounterType);
                                    }
                                    else
                                    {
                                        tempByName.Add(instance.Name, instance);
                                    }

                                    //Custom sum variable so that special ones can be summed up without interfering with total
                                    if (instance.Name != "_Total")
                                    {
                                        _Sum[counter] += instance.Value;
                                    }
                                }
                                tempByUsage = tempByName.Values.ToList();
                                tempByUsage.Sort();

                                if (this.ByName.ContainsKey(counter))
                                {
                                    this.ByName[counter] = tempByName;
                                }
                                else
                                {
                                    this.ByName.Add(counter, tempByName);
                                }
                                if (this.ByName.ContainsKey(counter))
                                {
                                    this.ByUsage[counter] = tempByUsage;
                                }
                                else
                                {
                                    this.ByUsage.Add(counter, tempByUsage);
                                }
                            }
                            else
                            {
                                API.Log((int)API.LogType.Debug, "Could not find a performance counter in " + options.Object + " called " + counter);
                            }
                        }
                    }
                    finally
                    {
                        Monitor.Exit(UpdateTimerLock);
                    }
                }
            }

            public InstanceLists(MeasureOptions options)
            {
                this.ByName = new Dictionary<String, Dictionary<String, Instance>>();
                this.ByUsage = new Dictionary<String, List<Instance>>();
                this.updateRates = new Dictionary<String, int> { { options.ID, options.UpdateInMS } };
                this.counters = new Dictionary<String, String> { { options.ID, options.Counter } };

                if (options.IsPID)
                {
                    pidIDs.Add(options.ID, options.UpdateInMS);
                    //@TODO This is still kinda slow, maybe going event based would be better

                    if (pidUpdateTimer == null || pidUpdateTimerRate > options.UpdateInMS)
                    {
                        pidUpdateTimerRate = options.UpdateInMS;
                        pidUpdateTimer = new Timer((stateInfo) => UpdatePIDs(), null, 0, options.UpdateInMS);
                    }
                }

                this.UpdateTimer = new Timer((stateInfo) => UpdateInstances(options), null, 0, options.UpdateInMS);
                this.UpdateTimerRate = options.UpdateInMS;
            }

            //Add new ID to instance and check update timer needs to be decrease (Will also update rate if an instance already existed)
            public void AddCounter(MeasureOptions options)
            {
                if (!this.updateRates.ContainsKey(options.ID))
                {
                    this.updateRates.Add(options.ID, options.UpdateInMS);

                    if (this.UpdateTimerRate > options.UpdateInMS)
                    {
                        this.UpdateTimer.Change(0, options.UpdateInMS);
                    }
                }
                else if(this.updateRates[options.ID] != options.UpdateInMS)
                    {
                    this.updateRates[options.ID] = options.UpdateInMS;

                    if (this.UpdateTimerRate > options.UpdateInMS)
                    {
                        if (this.UpdateTimer != null)
                        {
                            this.UpdateTimer.Change(0, options.UpdateInMS);
                        }
                        //Somehow timer got deintialized and we ended up here without it being reinitialized
                        else
                        {
                            this.UpdateTimer = new Timer((stateInfo) => UpdateInstances(options), null, 0, options.UpdateInMS);
                        }
                    }
                }

                if (!this.counters.ContainsKey(options.ID))
                {
                    this.counters.Add(options.ID, options.Counter);
                }
                else if (this.counters[options.ID] != options.Counter)
                {
                    this.counters[options.ID] = options.Counter;
                }
            }
            public void RemoveCounter(String ID)
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

        //This holds all the instances we are monitoring, when one stops being monitored it will be removed from the list
        //Each instance handles its own update
        public static Dictionary<String, InstanceLists> instances = new Dictionary<String, InstanceLists>(sizeof(MeasureAlias));

        //This is a list of all the PIDs and their associated process name, used to decode pids to process names
        private static Dictionary<int, String> pids = new Dictionary<int, String>();
        //Used to update pids, is set to lowest update rate of a instance that needs it
        //@TODO share resources with update timers using process category
        private static Timer pidUpdateTimer;
        private static int pidUpdateTimerRate = int.MaxValue;
        private static Object pidUpdateLock = new Object();
        private static void UpdatePIDs()
        {
            if (Monitor.TryEnter(pidUpdateLock))
            {
                try
                {
                    var pidInstance = new PerformanceCounterCategory("Process").ReadCategory()["ID Process"];
                    var temp = new Dictionary<int, String>(pidInstance.Count);

                    foreach (InstanceData pid in pidInstance.Values)
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
                            //PIDs should be unique but if they somehow are not log an error
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


        //Adds a new instance
        public static void AddCounter(MeasureOptions options)
        {
            if (options.Object != null && options.Counter != null)
            {
                //If it already exists just add the ID and update rate to the list
                if (instances.TryGetValue(options.Object, out InstanceLists instanceLists))
                {
                    instanceLists.AddCounter(options);
                }
                //If instance does not yet exist it will need to be created
                else
                {
                    instanceLists = new InstanceLists(options);
                    instances.Add(options.Object, instanceLists);
                }
            }
        }
        public static void RemoveCounter(MeasureOptions options)
        {
            if (options.Object != null && options.Counter != null)
            {
                //If instance exists remove ID from it
                if (instances.TryGetValue(options.Object, out InstanceLists instanceLists))
                {
                    instanceLists.RemoveCounter(options.ID);
                    //If nothing is referencing that instance anymore remove and deallocate it
                    if (instanceLists.Count() == 0)
                    {
                        instanceLists = null;
                        instances.Remove(options.Object);
                    }
                }
            }
        }
        public static bool GetInstanceLists(String perfObject, String counter, out InstanceLists instanceLists)
        {
            if (perfObject != null && counter != null)
            {
                return Instances.instances.TryGetValue(perfObject, out instanceLists);
            }
            instanceLists = null;
            return false;
        }
    }

    public class Plugin
    {
        [DllExport]
        public static void Initialize(ref IntPtr data, IntPtr rm)
        {
            data = GCHandle.ToIntPtr(GCHandle.Alloc(new MeasureOptions()));
            ((MeasureOptions)data).API = (Rainmeter.API)rm;
        }

        [DllExport]
        public static void Finalize(IntPtr data)
        {
            MeasureOptions options = (MeasureOptions)data;

            Instances.RemoveCounter(options);

            GCHandle.FromIntPtr(data).Free();
        }

        [DllExport]
        public static void Reload(IntPtr data, IntPtr rm, ref double maxValue)
        {
            MeasureOptions options = (MeasureOptions)data;
            options.API = (Rainmeter.API)rm;

            String aliasString = options.API.ReadString("Alias", "");
            MeasureAlias alias = MeasureAlias.CUSTOM;
            try
            {
                if (aliasString.Length > 0)
                {
                    alias = (MeasureAlias)Enum.Parse(typeof(MeasureAlias), aliasString, true);
                }
            }
            catch
            {
                options.API.Log(API.LogType.Error, "Type=" + aliasString + " was not valid,");
                alias = MeasureAlias.CPU;
            }
            options.DeAlias(alias);

            //Read what Performance Monitor info that we will be sampling
            String objectString = options.API.ReadString("Object", "");
            if (objectString.Length > 0)
            {
                options.Object = objectString;
            }
            String counterString = options.API.ReadString("Counter", "");
            if (counterString.Length > 0)
            {
                options.Counter = counterString;
            }

            //All the different options that change the way the info is measured/displayed
            //Rollup is on by default
            options.IsRollup = options.API.ReadInt("Rollup", Convert.ToInt32(options.IsRollup)) != 0;
            //Is precent is on by default when measure type is CPU
            options.IsPercent = options.API.ReadInt("Percent", Convert.ToInt32(options.IsPercent)) != 0;
            //Is pid is on by default when measure type is GPU or VRAM
            options.IsPID = options.API.ReadInt("PIDToName", Convert.ToInt32(options.IsPID)) != 0;
            //Get the update rate of the skin @TODO Make based on Update*UpdateRate*UpdateDivider
            options.UpdateInMS = options.API.ReadInt("UpdateRate", options.UpdateInMS);
            //ID of this options set
            options.ID = options.API.GetSkin() + options.API.GetMeasureName();

            //Setup new instance
            Instances.AddCounter(options);

            //One of these will be used later to access data
            options.Instance = options.API.ReadInt("Instance", -1);
            options.Name = options.API.ReadString("Name", null);
        }

        [DllExport]
        public static double Update(IntPtr data)
        {
            MeasureOptions options = (MeasureOptions)data;
            double ret = 0;
            
            if (Instances.GetInstanceLists(options.Object, options.Counter, out Instances.InstanceLists instances))
            {
                if (instances.ByUsage.Count > options.Instance && options.Instance >= 0 && instances.ByUsage.ContainsKey(options.Counter))
                {
                    ret = instances.ByUsage[options.Counter][options.Instance].Value;
                }
                else if (options.Name.Length > 0 && instances.ByUsage.ContainsKey(options.Counter) && instances.ByName[options.Counter].Count > 0)
                {
                    if(options.Name == "_Sum")
                    {
                        ret = instances._Sum[options.Counter];
                    }
                    else if (instances.ByName[options.Counter].TryGetValue(options.Name, out Instance instance))
                    {
                        ret = instance.Value;
                    }
                    else
                    {
                        options.API.Log(API.LogType.Debug, "Could not find a instance with the name " + options.Name);
                    }
                }
                else
                {
                    if (instances._Sum != null)
                    {
                        ret = instances._Sum[options.Counter];
                    }
                }

                if(options.IsPercent && instances.ByUsage.ContainsKey(options.Counter))
                {
                    if(instances.ByName[options.Counter].TryGetValue("_Total", out Instance instance) && instance.Value > 0)
                    {
                        ret = (ret / instance.Value) * 100;
                    }
                    else if (instances._Sum.ContainsKey(options.Counter) && instances._Sum[options.Counter] > 0)
                    {
                        ret = (ret / instances._Sum[options.Counter]) * 100;
                    }
                }
            }
            return ret;
        }

        [DllExport]
        public static IntPtr GetString(IntPtr data)
        {
            MeasureOptions options = (MeasureOptions)data;

            if (Instances.GetInstanceLists(options.Object, options.Counter, out Instances.InstanceLists instances))
            {
                if (instances.ByUsage.Count > options.Instance && options.Instance >= 0 && instances.ByUsage.ContainsKey(options.Counter))
                {
                    return Marshal.StringToHGlobalUni(instances.ByUsage[options.Counter][options.Instance].Name);
                }
                else if (instances.ByName.Count > 0 && options.Name.Length > 0 && instances.ByUsage.ContainsKey(options.Counter))
                {
                    //@TODO Should we maybe just always return the name?
                    if (options.Name == "_Sum")
                    {
                        return Marshal.StringToHGlobalUni("_Sum");
                    }
                    else if (instances.ByName[options.Counter].TryGetValue(options.Name, out Instance instance))
                    {
                        return Marshal.StringToHGlobalUni(instance.Name);
                    }
                    else
                    {
                        options.API.Log(API.LogType.Debug, "Could not find a instance with the name " + options.Name);
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
