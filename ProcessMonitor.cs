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
    public enum BlockType
    {
        N, //No blocking
        B, //Blacklist
        W //Whitelist
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
        //0 is no blocking, 1 is blacklist, 2 is whitelist
        public BlockType BlockType { get; private set; } = BlockType.B;
        public HashSet<String> BlockList { get; private set; } = new HashSet<string> { "_Total" };
        //This is the key that is used to identify a matching blocklist
        //It is the lists rollup status, blocktype, and list
        public String BlockString { get; private set; } = true.ToString() + BlockType.B + "|" + String.Join(",", new List<string> { "_Total" }.ToArray());
        public void UpdateBlockList(BlockType blockType, HashSet<String> blockList)
        {
            this.BlockType = blockType;
            this.BlockList = blockList;
            this.BlockString = IsRollup.ToString() + this.BlockType + "|" + String.Join(",", this.BlockList.ToArray());
        }
        public void UpdateBlockList(BlockType blockType, String blockList)
        {
            this.BlockType = blockType;
            this.BlockList = new HashSet<string>(blockList.Split(','));
            this.BlockString = IsRollup.ToString() + this.BlockType + "|" + blockList;
        }

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
        public double Value;
        public CounterSample Sample;

        public Instance(String Name, double Value, CounterSample Sample)
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

    //Since 3.5 does not have tuples
    //@TODO possibly make this a class and merge more into it
    public struct TimerInfo
    {
        public String ID;
        public int Rate;
        public TimerInfo(String ID, int Rate)
        {
            this.ID = ID;
            this.Rate = Rate;
        }
    }
    
    public class InstanceInfo
    {
        //ByName will either be with rollup on or off, then will be the list of all instances and their value
        public Dictionary<bool, Dictionary<String, Instance>> ByName;
        //ByUsage will be based on the white/blacklist status and the items in the list
        //List will start with W| or B| and will then list all items
        public Dictionary<string, List<Instance>> ByUsage;
        //_Sum is the same as ByUsage
        //@TODO Make a struct to merge these together
        public Dictionary<string, double> _Sum;
        public InstanceInfo()
        {
            //Key is roll up state
            this.ByName = new Dictionary<bool, Dictionary<string, Instance>>();

            //Key is block string
            this.ByUsage = new Dictionary<string, List<Instance>>();
            this._Sum = new Dictionary<string, double>();
        }
    }
    //Instances is basically a glorified dictionary that contains all the info and threads for updating each instance
    //Each instance is exposed in a ReadOnly fashion and is locked from reading during the write (Which is very short since temps are used)
    //Instances aggragate what measures are also using it and are only allocated and updated when a measure is still using them
    //@TODO Rename a bunch of stuff in this class by hand
    public static class PerfMon
    {
        //This holds all the instances we are monitoring, when one stops being monitored it will be removed from the list
        //Each instance handles its own update
        public static Dictionary<String, CounterLists> perfObjects = new Dictionary<String, CounterLists>(sizeof(MeasureAlias));

        public class CounterLists
        {
            //This is a dictionary, with counter as key, that contains a master list of all the different lists for this counter
            public Dictionary<String, InstanceInfo> masterList;
            //This is a dictionary, with counter as key, of all counters for this PerfMon object 
            //And a dictionary, with ID as key, of all the options of measures referencing that counter
            private Dictionary<String, Dictionary<String, MeasureOptions>> counterOptions;

            //This is the thread that will update its update rate dynamically to the lowest update rate a measure has set
            //@TODO Make Timer info sturct a class and merge the lock and thread into it
            private Timer UpdateTimer;
            private TimerInfo UpdateTimerInfo = new TimerInfo("", int.MaxValue);
            private Object UpdateTimerLock = new Object();

            //Function used to update instances
            //@TODO this loop could be slimmed up
            private void UpdateInstances(String objectGroup)
            {
                if (Monitor.TryEnter(UpdateTimerLock))
                {
                    try
                    {
                        //@TODO check if anything more can be done to make ReadCategory use less CPU time
                        var currObject = new PerformanceCounterCategory(objectGroup).ReadCategory();
                        Dictionary<String, InstanceInfo> tempMasterList = new Dictionary<string, InstanceInfo>(this.counterOptions.Count());
                        foreach (var counter in this.counterOptions)
                        {
                            var objectData = currObject[counter.Key];
                            var tempCounterList = new InstanceInfo();

                            foreach (var options in counter.Value.Values)
                            {
                                //Used to build the lists before adding to the master list
                                Dictionary<String, Instance> tempByName;
                                List<Instance> tempByUsage;
                                double _Sum = 0;

                                //If counter did not exist
                                if (objectData == null)
                                {
                                    API.Log((int)API.LogType.Debug, "Could not find a performance counter in " + objectGroup + " called " + counter);
                                }
                                //If there is already an ByName list that can be shared with this option set start from that
                                else if (tempCounterList.ByName.TryGetValue(options.IsRollup, out tempByName))
                                {
                                    //If there is not already a ByUsage list that can be shared with this option set then calculate a new one from ByName
                                    if (!tempCounterList.ByUsage.TryGetValue(options.BlockString, out tempByUsage))
                                    {
                                        tempByUsage = new List<Instance>();

                                        //@TODO this could be beter
                                        foreach (var instance in tempByName.Values.ToList())
                                        {
                                            //Check that either item is not in the blacklist or is in the whitelist
                                            if ((options.BlockType == BlockType.N)
                                                || (options.BlockType == BlockType.B && !options.BlockList.Contains(instance.Name))
                                                || (options.BlockType == BlockType.W && options.BlockList.Contains(instance.Name)))
                                            {
                                                tempByUsage.Add(instance);
                                                _Sum += instance.Value;
                                            }
                                        }
                                        tempByUsage.Sort();

                                        tempCounterList.ByUsage.Add(options.BlockString, tempByUsage);
                                        tempCounterList._Sum.Add(options.BlockString, _Sum);
                                    }
                                }
                                //If there was not already an ByName list that could be shared start for scratch
                                else
                                {
                                    tempByName = new Dictionary<string, Instance>(objectData.Count);
                                    bool hasLastUpdate = this.masterList.TryGetValue(counter.Key, out InstanceInfo lastInfo);

                                    foreach (InstanceData instanceData in objectData.Values)
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
                                        //If we are rolling up names then take the last # in the name and remove all after it
                                        if (options.IsRollup)
                                        {
                                            int index = instance.Name.LastIndexOf('#');
                                            if(index > 0)
                                            {
                                                instance.Name = instance.Name.Substring(0, index);
                                            }
                                        }
                                        
                                        //Check if we already have a instance with the same name, if we do combine the two before checking against last counter
                                        if (tempByName.TryGetValue(instance.Name, out Instance mergedInstance))
                                        {
                                            instance.Value += mergedInstance.Value;
                                            instance.Sample = new CounterSample(mergedInstance.Sample.RawValue + instance.Sample.RawValue,
                                                mergedInstance.Sample.BaseValue, mergedInstance.Sample.CounterFrequency,
                                                mergedInstance.Sample.SystemFrequency, mergedInstance.Sample.TimeStamp,
                                                mergedInstance.Sample.TimeStamp100nSec, mergedInstance.Sample.CounterType);
                                        }

                                        //I would love to use a null conditional here but then the compiler sees last measure as unassigned even though it would short the if statement out
                                        if (hasLastUpdate && lastInfo.ByName.TryGetValue(options.IsRollup, out Dictionary<string, Instance> lastMeasure)
                                            && lastMeasure.TryGetValue(instance.Name, out Instance lastInstance))
                                        {
                                            //If last update or this update did not have a raw value then assume it is still 0
                                            if (lastInstance.Sample.RawValue != 0 && instance.Sample.RawValue != 0)
                                            {
                                                instance = new Instance(instance.Name,
                                                    CounterSample.Calculate(lastInstance.Sample, instance.Sample), instance.Sample);
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

                                        //instance should be fully calculated at this point as go on ahead and update or add to the temp list
                                        if (mergedInstance == null)
                                        {
                                            tempByName.Add(instance.Name, instance);

                                            //Custom sum variable so that special ones can be summed up without interfering with total
                                            if (instance.Name != "_Total")
                                            {
                                                _Sum += instance.Value;
                                            }
                                        }
                                        else
                                        {
                                            tempByName[mergedInstance.Name] = instance;

                                            //Custom sum variable so that special ones can be summed up without interfering with total
                                            if (instance.Name != "_Total")
                                            {
                                                _Sum += instance.Value - mergedInstance.Value;
                                            }
                                        }
                                    }
                                    tempByUsage = new List<Instance>();

                                    //@TODO this could be beter
                                    foreach (var instance in tempByName.Values.ToList())
                                    {
                                        //Check that either item is not in the blacklist or is in the whitelist
                                        if ((options.BlockType == BlockType.N) 
                                            || (options.BlockType == BlockType.B && !options.BlockList.Contains(instance.Name))
                                            || (options.BlockType == BlockType.W && options.BlockList.Contains(instance.Name)))
                                        {
                                            tempByUsage.Add(instance);
                                        }
                                    }
                                    tempByUsage.Sort();

                                    tempCounterList.ByName.Add(options.IsRollup, tempByName);
                                    tempCounterList.ByUsage.Add(options.BlockString, tempByUsage);
                                    tempCounterList._Sum.Add(options.BlockString, _Sum);
                                }
                            }
                            tempMasterList.Add(counter.Key, tempCounterList);
                        }
                        masterList = tempMasterList;
                    }
                    finally
                    {
                        Monitor.Exit(UpdateTimerLock);
                    }
                }
            }

            public CounterLists(MeasureOptions options)
            {
                this.masterList = new Dictionary<string, InstanceInfo>();
                this.counterOptions = new Dictionary<string, Dictionary< String, MeasureOptions>> { { options.Counter, new Dictionary<string, MeasureOptions> { { options.ID, options } } } };

                if (options.IsPID)
                {
                    pidIDs.Add(options.ID, options.UpdateInMS);
                    //@TODO This is still kinda slow, maybe going event based would be better

                    if (pidUpdateTimer == null || pidUpdateTimerInfo.Rate > options.UpdateInMS)
                    {
                        pidUpdateTimerInfo = new TimerInfo(options.ID, options.UpdateInMS);
                        pidUpdateTimer = new Timer((stateInfo) => UpdatePIDs(), null, 0, pidUpdateTimerInfo.Rate);
                    }
                }

                this.UpdateTimerInfo = new TimerInfo(options.ID, options.UpdateInMS);
                this.UpdateTimer = new Timer((stateInfo) => UpdateInstances(options.Object), null, 0, this.UpdateTimerInfo.Rate);
            }

            //Add new ID to instance and check update timer needs to be decrease (Will also update rate if an instance already existed)
            public void AddCounter(MeasureOptions options)
            {
                //If counter is already being monitored
                if(this.counterOptions.TryGetValue(options.Counter, out Dictionary<String, MeasureOptions> counter))
                {
                    //If measure was already used and just needs values updated
                    if (counter.TryGetValue(options.ID, out MeasureOptions tempOptions))
                    {
                        //@TODO this is was more complex than just an update because what if counter or PerfMon object changes
                        //      thus there needs to be safeguards against that either here or more likely in the plugin object
                        tempOptions = options;
                    }
                    else
                    {
                        counter.Add(options.ID, options);
                    }
                }
                //If counter is not being monitored
                else
                {
                    counterOptions.Add(options.Counter, new Dictionary<string, MeasureOptions> { { options.ID, options } });
                }

                if (this.UpdateTimerInfo.Rate > options.UpdateInMS)
                {
                    this.UpdateTimerInfo = new TimerInfo(options.ID, options.UpdateInMS);
                    this.UpdateTimer.Change(0, this.UpdateTimerInfo.Rate);
                }
                if (options.IsPID)
                {
                    pidIDs.Add(options.ID, options.UpdateInMS);

                    if (pidUpdateTimer == null || pidUpdateTimerInfo.Rate > options.UpdateInMS)
                    {
                        pidUpdateTimerInfo = new TimerInfo(options.ID, options.UpdateInMS);
                        pidUpdateTimer = new Timer((stateInfo) => UpdatePIDs(), null, 0, pidUpdateTimerInfo.Rate);
                    }
                }
            }
            public void RemoveCounter(MeasureOptions options)
            {
                //If counter that needs to be removed exists
                if (this.counterOptions.TryGetValue(options.Counter, out Dictionary<String, MeasureOptions> counter))
                {
                    //If measure options are removed
                    if (counter.Remove(options.ID))
                    {
                        //If no more measures exist with this counter remove it
                        if (counter.Count() == 0)
                        {
                            this.counterOptions.Remove(options.Counter);
                        }


                        if (options.IsPID && pidIDs.Remove(options.ID))
                        {
                            //If nothing needs PID update stop thread
                            if (pidIDs.Count == 0)
                            {
                                pidUpdateTimer.Dispose();
                                pidUpdateTimer = null;
                                pidUpdateTimerInfo = new TimerInfo("", int.MaxValue);
                            }
                            else if (pidUpdateTimerInfo.ID == options.ID)
                            {
                                bool timerUpdated = false;
                                TimerInfo newTimerInfo = new TimerInfo("", int.MaxValue);
                                foreach (var tempCounter in this.counterOptions.Values)
                                {
                                    foreach (var tempOptions in tempCounter.Values)
                                    {
                                        if (options.UpdateInMS == pidUpdateTimerInfo.Rate)
                                        {
                                            timerUpdated = true;
                                            newTimerInfo = new TimerInfo(options.ID, options.UpdateInMS);
                                            break;
                                        }
                                        else if (options.UpdateInMS < newTimerInfo.Rate)
                                        {
                                            newTimerInfo = new TimerInfo(options.ID, options.UpdateInMS);
                                        }
                                    }
                                    if (timerUpdated == true)
                                    {
                                        break;
                                    }
                                }
                                //if the new rate is different then update the thread with new info
                                if (newTimerInfo.Rate != pidUpdateTimerInfo.Rate)
                                {
                                    pidUpdateTimerInfo = newTimerInfo;
                                    pidUpdateTimer.Change(0, pidUpdateTimerInfo.Rate);
                                }
                            }
                        }

                        //Only one timer just remove it and disable thread
                        if(this.counterOptions.Count() == 0)
                        {
                            this.UpdateTimer.Dispose();
                            this.UpdateTimer = null;
                            this.UpdateTimerInfo = new TimerInfo("", int.MaxValue);
                        }
                        else if(this.UpdateTimerInfo.ID == options.ID)
                        {
                            bool timerUpdated = false;
                            TimerInfo newTimerInfo = new TimerInfo("", int.MaxValue);
                            foreach (var tempCounter in this.counterOptions.Values)
                            {
                                foreach (var tempOptions in tempCounter.Values)
                                {
                                    if (options.UpdateInMS == this.UpdateTimerInfo.Rate)
                                    {
                                        timerUpdated = true;
                                        newTimerInfo = new TimerInfo(options.ID, options.UpdateInMS);
                                        break;
                                    }
                                    else if (options.UpdateInMS < newTimerInfo.Rate)
                                    {
                                        newTimerInfo = new TimerInfo(options.ID, options.UpdateInMS);
                                    }
                                }
                                if (timerUpdated == true)
                                {
                                    break;
                                }
                            }
                            this.UpdateTimerInfo = newTimerInfo;
                            this.UpdateTimer.Change(0, this.UpdateTimerInfo.Rate);
                        }
                    }
                }
            }
            public Instance GetInstance(MeasureOptions options, int instanceNumber)
            {
                if (masterList.TryGetValue(options.Counter, out InstanceInfo instanceInfo))
                {
                    if (instanceNumber == 0 && instanceInfo._Sum.TryGetValue(options.BlockString, out double value))
                    {
                        return new Instance("Total", value, new CounterSample());
                    }
                    //Instances in Rainmeter are not going to be 0 indexed so adjust them to be 0 indexed now
                    instanceNumber--;
                    if (instanceInfo.ByUsage.TryGetValue(options.BlockString, out List<Instance> tempByUsage))
                    {
                        if (tempByUsage.Count() > instanceNumber)
                        {
                            return tempByUsage[instanceNumber];
                        }
                    }
                }
                return new Instance("", 0, new CounterSample());
            }
            public Instance GetInstance(MeasureOptions options, String instanceName)
            {
                if (masterList.TryGetValue(options.Counter, out InstanceInfo instanceInfo))
                {
                    if (instanceInfo.ByName.TryGetValue(options.IsRollup, out Dictionary<String, Instance> tempByName))
                    {
                        if (tempByName.TryGetValue(instanceName, out Instance value))
                        {
                            return value;
                        }
                    }
                }
                return new Instance(instanceName, 0, new CounterSample());
            }
            public int Count()
            {
                //@TODO I am not sure this will work well enough
                return this.counterOptions.Count();
            }
        }

        //This is a list of all the PIDs and their associated process name, used to decode pids to process names
        private static Dictionary<int, String> pids = new Dictionary<int, String>();
        //List of all measures that need PIDs decoded
        private static Dictionary<String, int> pidIDs = new Dictionary<String, int>();
        //Used to update pids, is set to lowest update rate of a instance that needs it
        //@TODO share resources with update timers using process category
        private static Timer pidUpdateTimer;
        private static TimerInfo pidUpdateTimerInfo = new TimerInfo("", int.MaxValue);
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


        //Adds a new instance
        public static void AddCounter(MeasureOptions options)
        {
            if (options.Object != null && options.Counter != null)
            {
                //If it already exists just add the ID and update rate to the list
                if (perfObjects.TryGetValue(options.Object, out CounterLists instanceLists))
                {
                    instanceLists.AddCounter(options);
                }
                //If instance does not yet exist it will need to be created
                else
                {
                    instanceLists = new CounterLists(options);
                    perfObjects.Add(options.Object, instanceLists);
                }
            }
        }
        public static void RemoveCounter(MeasureOptions options)
        {
            if (options.Object != null && options.Counter != null)
            {
                //If instance exists remove ID from it
                if (perfObjects.TryGetValue(options.Object, out CounterLists instanceLists))
                {
                    instanceLists.RemoveCounter(options);
                    //If nothing is referencing that instance anymore remove and deallocate it
                    if (instanceLists.Count() == 0)
                    {
                        perfObjects.Remove(options.Object);
                    }
                }
            }
        }
        public static Instance GetInstance(MeasureOptions options, int instanceNumber)
        {
            if (perfObjects.TryGetValue(options.Object, out CounterLists instanceLists))
            {
                return instanceLists.GetInstance(options, instanceNumber);
            }
            return new Instance("", 0, new CounterSample());
        }
        public static Instance GetInstance(MeasureOptions options, String instanceName)
        {
            if (perfObjects.TryGetValue(options.Object, out CounterLists instanceLists))
            {
                return instanceLists.GetInstance(options, instanceName);
            }
            return new Instance(instanceName, 0, new CounterSample());
        }
        public static bool GetInstanceLists(String perfObject, String counter, out CounterLists instanceLists)
        {
            if (perfObject != null && counter != null)
            {
                return perfObjects.TryGetValue(perfObject, out instanceLists);
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

            PerfMon.RemoveCounter(options);

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
            String whitelist = options.API.ReadString("Whitelist", "");
            if (whitelist.Length > 0)
            {
                options.UpdateBlockList(BlockType.W, whitelist);
            }
            else
            {
                String blacklist = options.API.ReadString("Blacklist", "_Total");
                if (blacklist.Length > 0)
                {
                    options.UpdateBlockList(BlockType.B, blacklist);
                }
                else
                {
                    options.UpdateBlockList(BlockType.N, "");
                }
            }
            //Is precent is on by default when measure type is CPU
            options.IsPercent = options.API.ReadInt("Percent", Convert.ToInt32(options.IsPercent)) != 0;
            if(options.IsPercent)
            {
                maxValue = 100;
            }
            //Is pid is on by default when measure type is GPU or VRAM
            options.IsPID = options.API.ReadInt("PIDToName", Convert.ToInt32(options.IsPID)) != 0;
            //Get the update rate of the skin @TODO Make based on Update*UpdateRate*UpdateDivider
            options.UpdateInMS = options.API.ReadInt("UpdateRate", options.UpdateInMS);
            //ID of this options set
            options.ID = options.API.GetSkin() + options.API.GetMeasureName();

            //Setup new instance
            PerfMon.AddCounter(options);

            //One of these will be used later to access data
            options.Instance = options.API.ReadInt("Instance", -1);
            options.Name = options.API.ReadString("Name", null);
        }

        [DllExport]
        public static double Update(IntPtr data)
        {
            MeasureOptions options = (MeasureOptions)data;
            double ret = 0;
            
            if (options.Instance >= 0)
            {
                ret = PerfMon.GetInstance(options, options.Instance).Value;
            }
            else if (options.Name.Length > 0)
            {
                ret = PerfMon.GetInstance(options, options.Name).Value;
            }
            //Scale it to be out of 100% if user requests it
            //@TODO have an option to make this _Sum based?
            if (options.IsPercent)
            {
                double sum = PerfMon.GetInstance(options, "_Total").Value;

                //ret is 0 if it would be NaN
                ret = sum > 0 ? ret / sum * 100 : 0;
            }

            return ret;
        }

        [DllExport]
        public static IntPtr GetString(IntPtr data)
        {
            MeasureOptions options = (MeasureOptions)data;
            
            if (options.Instance >= 0)
            {
                return Marshal.StringToHGlobalUni(PerfMon.GetInstance(options, options.Instance).Name);
            }
            else if (options.Name.Length > 0)
            {
                return Marshal.StringToHGlobalUni(PerfMon.GetInstance(options, options.Name).Name);
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
