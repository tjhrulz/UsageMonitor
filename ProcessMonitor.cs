﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Rainmeter;

using System.Diagnostics;
using System.Linq;
using System.Collections;
using System.Threading;

namespace ProcessMonitor
{
    //These are the supported aliases, these are used to make it easier for skins to just get a specific category
    //Note that Netdown & Netup are unimplemented
    public enum MeasureAlias
    {
        CPU,
        RAM,
        RAMSHARED,
        IO,
        IOREAD,
        IOWRITE,
        GPU,
        VRAM,
        NETDOWN,
        NETUP,
        CUSTOM
    }
    //Types of blocklists
    public enum BlockType
    {
        N, //No blocking
        B, //Blacklist
        W //Whitelist
    }
    //Contains all the options for the measure
    public class MeasureOptions
    {
        static public implicit operator MeasureOptions(IntPtr data)
        {
            return (MeasureOptions)GCHandle.FromIntPtr(data).Target;
        }
        public Rainmeter.API API;
        //One of these will normally be empty/-1
        public int Instance;
        public String Name;

        //These are all the default values of the options (Some are overiden for certain aliases)
        public bool IsPercent = false;
        public bool IsPID = false;
        public bool IsRollup = true;
        public BlockType BlockType { get; private set; } = BlockType.B;
        public HashSet<String> BlockList { get; private set; } = new HashSet<string> { "_Total" };
        //This is the key that is used to identify if a blocklist can be shared, it is the options rollup status, blocktype, and list
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

        //Update rate will be by default 1 second as that is the fastest PerfMon updates
        public int UpdateInMS = 1000;

        //Used to identify the measure that had this option group
        public string ID;
        public String Category;
        public String Counter;

        //Takes an alias and changes the options to be ideal for that alias
        //@TODO add NETUP and NETDOWN support
        public void DeAlias(MeasureAlias alias)
        {
            if (alias == MeasureAlias.CPU)
            {
                this.Category = "Process";
                this.Counter = "% Processor Time";
                this.IsPercent = true;
            }
            else if (alias == MeasureAlias.RAM)
            {
                this.Category = "Process";
                this.Counter = "Working Set - Private";
            }
            else if (alias == MeasureAlias.RAMSHARED)
            {
                this.Category = "Process";
                this.Counter = "Working Set";
            }
            else if (alias == MeasureAlias.IO)
            {
                this.Category = "Process";
                this.Counter = "IO Data Bytes/sec";
            }
            else if (alias == MeasureAlias.IOREAD)
            {
                this.Category = "Process";
                this.Counter = "IO Read Bytes/sec";
            }
            else if (alias == MeasureAlias.IOWRITE)
            {
                this.Category = "Process";
                this.Counter = "IO Write Bytes/sec";
            }
            else if (alias == MeasureAlias.GPU)
            {
                this.Category = "GPU Engine";
                this.Counter = "Utilization Percentage";
                this.IsPID = true;
            }
            else if (alias == MeasureAlias.VRAM)
            {
                this.Category = "GPU Process Memory";
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
    
    //This class handles taking a measures options and turning it in to an organized list of performance catagories that it then keeps up to date
    //It is highly scalable and multithreaded and will share as many resources as possible between measures
    //Resouce sharing is reduced slightly when two measures dont share the same block list, even more when they are not the same rollup state
    //Minimal resources are shared between measures with different catagories and each category will run on its own thread
    public static class Catagories
    {
        //This holds all the catagories we are monitoring, when one stops being monitored it will be removed from the dictionary
        //Each category in here will be updated on its own thread
        private static Dictionary<String, Counters> CatagoriesCounters = new Dictionary<String, Counters>(sizeof(MeasureAlias));

        //Used for the timer info since .Net 3.5 does not have tuples
        //@TODO merge the timer class and timer variables to be part of the same class within CategoryLists
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

        private class Counters
        {
            //Locks data from being read when 
            private Object dataLock = new Object();

            //This class is mainly just to tidy things up while building the lists that end up being exposed
            private class CounterInfo
            {
                //Key for ByName will be the rollup status of the instaces, with the value being the dictionary of the instances by name
                public Dictionary<bool, Dictionary<String, Instance>> ByName;
                //Key for ByName will be the blocklist of the instaces, with the value being a list of the instances sorted by value
                public Dictionary<string, List<Instance>> ByUsage;
                //Key for _Sum will be the blocklist of the instaces, with the value being the sum of that lists value
                //I could pack this into the ByUsage list but it seemed pointless
                public Dictionary<string, double> Sum;
                public CounterInfo(int count)
                {
                    //Key is roll up state as two dictionaries of names can not be shared if they are rolled up differently as they have different values
                    //NOTE: ByName contains everything, even if it would have been ignored by the blocklist
                    this.ByName = new Dictionary<bool, Dictionary<string, Instance>>(count);

                    //Key is block string as the list/sum can not be shared if they contain different instances
                    this.ByUsage = new Dictionary<string, List<Instance>>(count);
                    this.Sum = new Dictionary<string, double>(count);
                }
            }

            //This is the collection of all the counters and their instances in formatted collection organized by different possible options
            //Key is the name of the counter, with the value being the all the info for the measures using that counter
            private Dictionary<String, CounterInfo> CountersInfo;
            //This is the collection of all the counters and the options from the measures referencing it
            //Key is the specific counter with its value being the ID of the measure that gave us the option set and the option set itself
            //@TODO Merge this into CounterInfo class
            private Dictionary<String, Dictionary<String, MeasureOptions>> CounterOptions;

            //These control the thread in change of updating catagories, its update its update rate dynamically adjust based on lowest rate currently active
            private Timer UpdateTimer;
            private TimerInfo UpdateTimerInfo = new TimerInfo("", int.MaxValue);
            private Object UpdateTimerLock = new Object();


            //Function used to update all info for given category
            //Will format the info then based on current option sets that are using that category
            private void UpdateCategory(String category)
            {
                if (Monitor.TryEnter(UpdateTimerLock))
                {
                    try
                    {
                        //This is the most expensive part accounting for a very large amount of the CPU, this is avoided at all costs
                        //Avoiding doing this is why this is why this loop is so complex
                        InstanceDataCollectionCollection currCategoryData = new PerformanceCounterCategory(category).ReadCategory();

                        //This will be our collection of counters for this category, we will build in this temp collection before changing the pointer at the end
                        Dictionary<String, CounterInfo> tempCounters = new Dictionary<string, CounterInfo>(this.CounterOptions.Count());

                        //Loop through each counter
                        foreach (var currCounter in this.CounterOptions)
                        {
                            //All the instances for this counter unformatted from PerfMon
                            InstanceDataCollection counterInstances = currCategoryData[currCounter.Key];
                            //Temp counter info collecetions that we will build on in the option set loop
                            CounterInfo tempCounterInfo = new CounterInfo(currCounter.Value.Count());

                            //Loop through each option set for the current counter
                            foreach (var options in currCounter.Value.Values)
                            {
                                //Instances will be added to these collections before being added to tempCounterInfo under the collection with the same name
                                //They will be added to their collection with a key of what type of option it was formatted under
                                Dictionary<String, Instance> tempByName;
                                List<Instance> tempByUsage;
                                double tempSum = 0;

                                //If counter did not exist for this category
                                if (counterInstances == null)
                                {
                                    //Throw and error and add nothing to the temp collections
                                    options.API.Log(API.LogType.Error, "Could not find a counter in catagory " + category + " called " + currCounter);
                                }
                                //If there is already an ByName list that can be shared with this option set start from that
                                else if (tempCounterInfo.ByName.TryGetValue(options.IsRollup, out tempByName))
                                {
                                    //If there is not already a ByUsage list that can be shared with this option set then calculate a new one from ByName
                                    if (!tempCounterInfo.ByUsage.TryGetValue(options.BlockString, out tempByUsage))
                                    {
                                        tempByUsage = new List<Instance>();

                                        //Generate a new ByUsage list that complies with this option set's blocklist
                                        foreach (var instance in tempByName.Values.ToList())
                                        {
                                            //Check that either item is not in the blacklist or is in the whitelist
                                            if ((options.BlockType == BlockType.N)
                                                || (options.BlockType == BlockType.B && !options.BlockList.Contains(instance.Name))
                                                || (options.BlockType == BlockType.W && options.BlockList.Contains(instance.Name)))
                                            {
                                                tempByUsage.Add(instance);
                                                tempSum += instance.Value;
                                            }
                                        }
                                        tempByUsage.Sort();

                                        //Add to temp collection as we are done
                                        tempCounterInfo.ByUsage.Add(options.BlockString, tempByUsage);
                                        tempCounterInfo.Sum.Add(options.BlockString, tempSum);
                                    }
                                    //If there was then we are done as nothing more needs done
                                }
                                //If there was not already an ByName list that could be shared start for scratch
                                else
                                {
                                    tempByName = new Dictionary<string, Instance>(counterInstances.Count);
                                    bool hasLastUpdate = this.CountersInfo.TryGetValue(currCounter.Key, out CounterInfo lastInfo);

                                    //Go through each instance and format the data for use with Rainmeter such as PID translation & interpreting raw values
                                    foreach (InstanceData instanceData in counterInstances.Values)
                                    {
                                        Instance instance = new Instance(instanceData.InstanceName, instanceData.RawValue, instanceData.Sample);

                                        //Instance name is a PID and needs to be converted to a process name
                                        //NOTE: If we found we needed to translate different looking PIDs then that would go here
                                        if (options.IsPID)
                                        {
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
                                                    options.API.Log(API.LogType.Debug, "Could not find a process with PID of " + myPid + " this PID will be ignored till found");
                                                    continue;
                                                }
                                            }
                                        }

                                        //If we are rolling up similar names then take the last # in the name and remove all after it
                                        //NOTE: If we wanted to add another way to rollup names it would go here
                                        if (options.IsRollup)
                                        {
                                            int index = instance.Name.LastIndexOf('#');
                                            if (index > 0)
                                            {
                                                instance.Name = instance.Name.Substring(0, index);
                                            }
                                        }

                                        //Check if we already have a instance with the same name, if we do combine the two now
                                        if (tempByName.TryGetValue(instance.Name, out Instance mergedInstance))
                                        {
                                            instance.Value += mergedInstance.Value;
                                            instance.Sample = new CounterSample(mergedInstance.Sample.RawValue + instance.Sample.RawValue,
                                                mergedInstance.Sample.BaseValue, mergedInstance.Sample.CounterFrequency,
                                                mergedInstance.Sample.SystemFrequency, mergedInstance.Sample.TimeStamp,
                                                mergedInstance.Sample.TimeStamp100nSec, mergedInstance.Sample.CounterType);
                                        }

                                        //If there is a valid last update value and the current is value process the value to be readable
                                        //If there is not then set the value to 0
                                        if (hasLastUpdate && lastInfo.ByName.TryGetValue(options.IsRollup, out Dictionary<string, Instance> lastMeasure)
                                            && lastMeasure.TryGetValue(instance.Name, out Instance lastInstance))
                                        {
                                            if (lastInstance.Sample.RawValue != 0 && instance.Sample.RawValue != 0)
                                            {
                                                instance.Value = CounterSample.Calculate(lastInstance.Sample, instance.Sample);
                                            }
                                            else
                                            {
                                                instance.Value = 0;
                                            }
                                        }
                                        else
                                        {
                                            instance.Value = 0;
                                        }

                                        //Since instance is human readable at this point go on ahead and either update the old value or add the value to the collection
                                        if (mergedInstance == null)
                                        {
                                            tempByName.Add(instance.Name, instance);
                                        }
                                        else
                                        {
                                            tempByName[mergedInstance.Name] = instance;
                                        }
                                    }
                                    tempByUsage = new List<Instance>();

                                    //Generate a ByUsage list that complies with this option set's blocklist
                                    foreach (var instance in tempByName.Values.ToList())
                                    {
                                        //Check that either item is not in the blacklist or is in the whitelist
                                        if ((options.BlockType == BlockType.N)
                                            || (options.BlockType == BlockType.B && !options.BlockList.Contains(instance.Name))
                                            || (options.BlockType == BlockType.W && options.BlockList.Contains(instance.Name)))
                                        {
                                            tempByUsage.Add(instance);
                                            tempSum += instance.Value;
                                        }
                                    }
                                    tempByUsage.Sort();

                                    //Add to temp collection as we are done
                                    tempCounterInfo.ByName.Add(options.IsRollup, tempByName);
                                    tempCounterInfo.ByUsage.Add(options.BlockString, tempByUsage);
                                    tempCounterInfo.Sum.Add(options.BlockString, tempSum);
                                }
                            }

                            //Add newly formatted counter collections to the collection of counters
                            tempCounters.Add(currCounter.Key, tempCounterInfo);
                        }

                        //Make tempCounter the current counter list
                        lock (dataLock)
                        {
                            CountersInfo = tempCounters;
                        }
                    }
                    catch
                    {
                        this.CounterOptions.First().Value.First().Value.API.Log(API.LogType.Error, "Could not find a catagory called " + category);
                    }
                    finally
                    {
                        Monitor.Exit(UpdateTimerLock);
                    }
                }
            }

            //Constructor for the counters object, assumes all counters added to this object are within the same category
            public Counters(MeasureOptions options)
            {
                this.CountersInfo = new Dictionary<string, CounterInfo>();
                this.CounterOptions = new Dictionary<string, Dictionary<String, MeasureOptions>> { { options.Counter, new Dictionary<string, MeasureOptions> { { options.ID, options } } } };

                if (options.IsPID)
                {
                    pidIDs.Add(options.ID, options.UpdateInMS);

                    if (pidUpdateTimer == null || pidUpdateTimerInfo.Rate > options.UpdateInMS)
                    {
                        pidUpdateTimerInfo = new TimerInfo(options.ID, options.UpdateInMS);
                        pidUpdateTimer = new Timer((stateInfo) => UpdatePIDs(), null, 0, pidUpdateTimerInfo.Rate);
                    }
                }

                this.UpdateTimerInfo = new TimerInfo(options.ID, options.UpdateInMS);
                this.UpdateTimer = new Timer((stateInfo) => UpdateCategory(options.Category), null, 0, this.UpdateTimerInfo.Rate);
            }

            //Add new counter to monitor (Assumes added to counters object with same category)
            //Will also change update rate if new one is lower
            public void AddCounter(MeasureOptions options)
            {
                //If counter is already being monitored
                if (this.CounterOptions.TryGetValue(options.Counter, out Dictionary<String, MeasureOptions> counter))
                {
                    //If measure was already used and just needs values updated
                    if (counter.TryGetValue(options.ID, out MeasureOptions tempOptions))
                    {
                        //@TODO this is was more complex than just an update because what if category or PerfMon counter changes
                        //      thus there needs to be safeguards against that either here or more likely in the plugin counter
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
                    CounterOptions.Add(options.Counter, new Dictionary<string, MeasureOptions> { { options.ID, options } });
                }

                if (this.UpdateTimerInfo.Rate > options.UpdateInMS)
                {
                    this.UpdateTimerInfo = new TimerInfo(options.ID, options.UpdateInMS);
                    this.UpdateTimer.Change(0, this.UpdateTimerInfo.Rate);
                }
                //Since measures are unloaded in the same order they are loaded keep the last measure that was added to prevent ID thrashing when a skin is unloaded
                else if(this.UpdateTimerInfo.Rate == options.UpdateInMS)
                {
                    this.UpdateTimerInfo.ID = options.ID;
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
            //Removes a a reference to a given counter (Counter is only removed if it is the last reference to that counter) 
            //Will also change update rate if the one removed was the last one with that update rate
            public void RemoveCounter(MeasureOptions options)
            {
                //If category that needs to be removed exists
                if (this.CounterOptions.TryGetValue(options.Counter, out Dictionary<String, MeasureOptions> counter))
                {
                    //If measure options are removed
                    if (counter.Remove(options.ID))
                    {
                        //If no more measures exist with this category remove it
                        if (counter.Count() == 0)
                        {
                            this.CounterOptions.Remove(options.Counter);
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
                                foreach (var tempCounter in this.CounterOptions.Values)
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
                        if (this.CounterOptions.Count() == 0)
                        {
                            this.UpdateTimer.Dispose();
                            this.UpdateTimer = null;
                            this.UpdateTimerInfo = new TimerInfo("", int.MaxValue);
                        }
                        else if (this.UpdateTimerInfo.ID == options.ID)
                        {
                            bool timerUpdated = false;
                            TimerInfo newTimerInfo = new TimerInfo("", int.MaxValue);
                            foreach (var tempCategory in this.CounterOptions.Values)
                            {
                                foreach (var tempOptions in tempCategory.Values)
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
            //Get an instance of a counter ordered by value
            public Instance GetInstance(MeasureOptions options, int instanceNumber)
            {
                lock (dataLock)
                {
                    if (CountersInfo.TryGetValue(options.Counter, out CounterInfo counterInfo))
                    {
                        if (instanceNumber == 0 && counterInfo.Sum.TryGetValue(options.BlockString, out double value))
                        {
                            return new Instance("Total", value, new CounterSample());
                        }
                        //Instances in Rainmeter are not going to be 0 indexed so adjust them to be 0 indexed now
                        instanceNumber--;
                        if (counterInfo.ByUsage.TryGetValue(options.BlockString, out List<Instance> tempByUsage))
                        {
                            if (tempByUsage.Count() > instanceNumber)
                            {
                                return tempByUsage[instanceNumber];
                            }
                        }
                    }
                }
                return new Instance("", 0, new CounterSample());
            }
            //Get an instance of a counter by name
            public Instance GetInstance(MeasureOptions options, String instanceName)
            {
                lock (dataLock)
                {
                    if (CountersInfo.TryGetValue(options.Counter, out CounterInfo counterInfo))
                    {
                        if (counterInfo.ByName.TryGetValue(options.IsRollup, out Dictionary<String, Instance> tempByName))
                        {
                            if (tempByName.TryGetValue(instanceName, out Instance value))
                            {
                                return value;
                            }
                        }
                    }
                }
                return new Instance(instanceName, 0, new CounterSample());
            }
            public int Count()
            {
                return this.CounterOptions.Count();
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
        //This handles keeping the PID list up to date
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
                            API.Log((int)API.LogType.Debug, "ProcessMonitor had a process ID collision on PID " + pid);
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
        
        //Parses a new MeasureOption set and creates/adds it to a catefory and its collection of counters it is being used for
        public static void AddMeasure(MeasureOptions options)
        {
            if (options.Category != null && options.Counter != null)
            {
                //If it already exists just add the ID and update rate to the list
                if (CatagoriesCounters.TryGetValue(options.Category, out Counters counters))
                {
                    counters.AddCounter(options);
                }
                //If instance does not yet exist it will need to be created
                else
                {
                    counters = new Counters(options);
                    CatagoriesCounters.Add(options.Category, counters);
                }
            }
        }
        //Removes MeasureOption set and destroys the category if it is the last option using it
        public static void RemoveMeasure(MeasureOptions options)
        {
            if (options.Category != null && options.Counter != null)
            {
                //If instance exists remove ID from it
                if (CatagoriesCounters.TryGetValue(options.Category, out Counters counters))
                {
                    counters.RemoveCounter(options);
                    //If nothing is referencing that instance anymore remove and deallocate it
                    if (counters.Count() == 0)
                    {
                        CatagoriesCounters.Remove(options.Category);
                    }
                }
            }
        }
        //Get an instance of a counter ordered by value
        public static Instance GetInstance(MeasureOptions options, int instanceNumber)
        {
            if (CatagoriesCounters.TryGetValue(options.Category, out Counters instanceLists))
            {
                return instanceLists.GetInstance(options, instanceNumber);
            }
            return new Instance("", 0, new CounterSample());
        }
        //Get an instance of a counter by name
        public static Instance GetInstance(MeasureOptions options, String instanceName)
        {
            if (CatagoriesCounters.TryGetValue(options.Category, out Counters instanceLists))
            {
                return instanceLists.GetInstance(options, instanceName);
            }
            return new Instance(instanceName, 0, new CounterSample());
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

            Catagories.RemoveMeasure(options);

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
            String categoryString = options.API.ReadString("Category", "");
            if (categoryString.Length > 0)
            {
                options.Category= categoryString;
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
            if (options.IsPercent)
            {
                maxValue = 100;
            }
            //Is pid is on by default when measure type is GPU or VRAM
            options.IsPID = options.API.ReadInt("PIDToName", Convert.ToInt32(options.IsPID)) != 0;
            options.UpdateInMS = options.API.ReadInt("UpdateRate", options.UpdateInMS);
            //ID of this options set
            options.ID = options.API.GetSkin() + options.API.GetMeasureName();

            //Setup new instance
            Catagories.AddMeasure(options);

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
                ret = Catagories.GetInstance(options, options.Instance).Value;
            }
            else if (options.Name.Length > 0)
            {
                ret = Catagories.GetInstance(options, options.Name).Value;
            }
            //Scale it to be out of 100% if user requests it
            //@TODO have an option to make this _Sum based?
            if (options.IsPercent)
            {
                double sum = Catagories.GetInstance(options, "_Total").Value;

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
                return Marshal.StringToHGlobalUni(Catagories.GetInstance(options, options.Instance).Name);
            }
            else if (options.Name.Length > 0)
            {
                return Marshal.StringToHGlobalUni(Catagories.GetInstance(options, options.Name).Name);
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
