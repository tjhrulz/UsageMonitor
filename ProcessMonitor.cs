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
    //@TODO Add support for custom types
    //@TODO Have these use actual strings when set 
    public enum MeasureType
    {
        CPU,
        RAM,
        IO,
        GPU,
        VRAM,
        NETDOWN,
        NETUP
    }
    public class MeasureCatagory
    {
        public string Catagory;
        public string SubCatagory;

        public MeasureCatagory(MeasureType type)
        {
            if(type == MeasureType.CPU)
            {
                Catagory = "Process";
                SubCatagory = "% Processor Time";
            }
            else if (type == MeasureType.RAM)
            {
                Catagory = "Process";
                SubCatagory = "Working Set";
            }
            else if (type == MeasureType.IO)
            {
                Catagory = "Process";
                SubCatagory = "IO Data Bytes/sec";
            }
            else if (type == MeasureType.GPU)
            {
                Catagory = "GPU Engine";
                SubCatagory = "Utilization Percentage";
            }
            else if (type == MeasureType.VRAM)
            {
                Catagory = "GPU Process Memory";
                SubCatagory = "Dedicated Usage";
            }
        }
    }

    //Custom counter that implements IComparable so that it can be easily sorted
    public class Counter : IComparable<Counter>
    {
        public string Name;
        public long Value;

        public Counter(string Name, long Value)
        {
            this.Name = Name;
            this.Value = Value;
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
    //Counters aggragate what skins are also using it and are only allocated and updated when a measure is still using them
    //@TODO store skins that whitelist and blacklist a process here?
    public static class Counters
    {
        public class CounterLists
        {
            //Both a list sorted by usage and a dictionary are able to be used to access the processes 
            public Dictionary<string, Counter> ByName;
            public List<Counter> ByUsage;

            //This is a list of all the skins using the same counters and what each ones update rate is (The lowest rate is the one used
            private Dictionary<IntPtr, int> Skinptrs;
            //This is the thread that will update its update rate dynamically to the lowest update rate a skin has set
            private Timer UpdateTimer;

            public CounterLists(string catagory, string subCatagory, IntPtr skinptr, bool isPID, int updateInMS)
            {
                this.ByName = new Dictionary<string, Counter>();
                this.ByUsage = new List<Counter>();
                this.Skinptrs = new Dictionary<IntPtr, int>();
                this.Skinptrs.Add(skinptr, updateInMS);

                //@TODO This is still pretty slow, maybe going event based would be better
                pidUpdateTimer = new Timer((stateInfo) =>
                {
                    if (isPID)
                    {
                        var pidCounter = new PerformanceCounterCategory("Process").ReadCategory()["ID Process"];
                        pids = new Dictionary<int, string>(pidCounter.Count);

                        foreach (InstanceData pid in pidCounter.Values)
                        {
                            try
                            {
                                //Both Idle and _Total share a PID, ignore them
                                if (pid.RawValue != 0)
                                {
                                    pids.Add((int)pid.RawValue, pid.InstanceName);
                                }
                            }
                            catch
                            {
                                //PIDs should be unique but if they somehow are not throw an error
                                API.Log((int)API.LogType.Error, "Found another process with the pid of" + pid);
                            }
                        }
                    }
                }, null, 0, updateInMS);

                this.UpdateTimer = new Timer((stateInfo) =>
                {
                    //@TODO Use temp for calculations and lock while writing
                    var temp = new PerformanceCounterCategory(catagory).ReadCategory()[subCatagory];
                    this.ByName = new Dictionary<string, Counter>(temp.Count);
                    this.ByUsage = new List<Counter>(temp.Count);
                    foreach (InstanceData instance in temp.Values)
                    {
                        Counter counter = new Counter(instance.InstanceName, instance.RawValue);
                        //Counter name is a PID and needs to be converted to a process name
                        if(isPID)
                        {
                            //"pid_12952_luid_0x00000000_0x00009AC6_phys_0_eng_0_engtype_3D"
                            //"pid_11528_luid_0x00000000_0x0000A48E_phys_0"
                            //This could be more hard coded but I wanted to be more versitile;
                            int start = counter.Name.IndexOf("pid_") + "pid_".Length;
                            int end = counter.Name.IndexOf("_", start);

                            if(Int32.TryParse(counter.Name.Substring(start, end - start), out int myPid))
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
                                    API.Log((int)API.LogType.Error, "Could not find a process with PID of " + myPid);
                                }
                            }
                        }
                        if(this.ByName.ContainsKey(counter.Name))
                        {
                            this.ByName[counter.Name].Value += counter.Value;
                        }
                        else
                        {
                            this.ByName.Add(counter.Name, counter);
                        }
                    }
                    //@TODO check performance of this
                    this.ByUsage = this.ByName.Values.ToList();
                    this.ByUsage.Sort();
                }, null, 0, updateInMS);
            }

            public void AddSkin(IntPtr skinptr, int updateInMS)
            {
                //@TODO Implement, need to check if update rate changes during add
                //@TODO check if isPID and if it is then see if that update rate needs to be changed
                return;
            }
            public void RemoveSkin(IntPtr skinptr)
            {
                //@TODO Implement, need to check if update rate changes during remove and nulled out if no remaining skins use it
                //@TODO Check if last skin with isPID and if so stop thread
                return;
            }
        }

        //This holds all the counters we are monitoring, when one stops being monitored it will be removed from the list
        //Each counter handles its own update
        public static Dictionary<string, CounterLists> counters = new Dictionary<string, CounterLists>(sizeof(MeasureType));

        //This is a list of all the PIDs and their associated process name, used to decode pids to process names
        private static Dictionary<int, string> pids = new Dictionary<int, string>();
        //Used to update pids, is set whenever a skin needs it to the update rate of that skin
        private static Timer pidUpdateTimer;


        //Adds a new counter
        public static void AddCounter(string catagory, string subCatagory, IntPtr skinptr, bool isPID = false, int updateInMS = 1000)
        {
            //If it already exists just add the skinptr and update rate to the list
            if (counters.TryGetValue(catagory + '|' + subCatagory, out CounterLists counter))
            {
                counter.AddSkin(skinptr, updateInMS);
            }
            //If counter does not yet exist it will need to be created
            else
            {
                CounterLists newCounter = new CounterLists(catagory, subCatagory, skinptr, isPID, updateInMS);
                counters.Add(catagory + '|' + subCatagory, newCounter);
            }

        }
        public static void RemoveCounter(string catagory, string subCatagory, IntPtr skinptr)
        {
            //If counter exists remove skinptr from it (Which will remove counter if it is the last skin
            if(counters.TryGetValue(catagory + '|' + subCatagory, out CounterLists counter))
            {
                counter.RemoveSkin(skinptr);
            }
        }
        public static CounterLists GetCounter(string catagory, string subCatagory)
        {
            return counters[catagory +'|'+ subCatagory];
        }
    }

    class Measure
    {
        static public implicit operator Measure(IntPtr data)
        {
            return (Measure)GCHandle.FromIntPtr(data).Target;
        }
        public Rainmeter.API API;

        public MeasureCatagory myCatagories;
        public int myInstance;
        public string myName;
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
                //@TODO check if type is custom and if it is then run custom setup
                MeasureType type = (MeasureType)Enum.Parse(typeof(MeasureType), measure.API.ReadString("Type", "CPU"), true);
                measure.myCatagories = new MeasureCatagory(type);
                if(type == MeasureType.GPU || type == MeasureType.VRAM)
                {
                    isPID = true;
                }
            }
            catch
            {
                measure.API.Log(API.LogType.Error, "Type " + measure.API.ReadString("Type", "CPU") + " was not in the list of predefined catagories, assuming CPU");
                measure.myCatagories = new MeasureCatagory(MeasureType.CPU);
            }

            measure.myInstance = measure.API.ReadInt("Instance", -1);
            measure.myName = measure.API.ReadString("Name", null);

            Counters.AddCounter(measure.myCatagories.Catagory, measure.myCatagories.SubCatagory, measure.API.GetSkin(),isPID);
        }

        [DllExport]
        public static double Update(IntPtr data)
        {
            Measure measure = (Measure)data;
            
            if (Counters.counters.TryGetValue(measure.myCatagories.Catagory + "|" + measure.myCatagories.SubCatagory, out Counters.CounterLists counters))
            {
                if (counters.ByUsage.Count > measure.myInstance && measure.myInstance >= 0)
                {
                    return counters.ByUsage[measure.myInstance].Value;
                }
                else if(counters.ByName.Count > 0 && measure.myName.Length > 0)
                {
                    if (counters.ByName.TryGetValue(measure.myName, out Counter counter))
                    {
                        return counter.Value;
                    }
                    else
                    {
                        measure.API.Log(API.LogType.Debug, "Could not find a counter with the name " + measure.myName);
                    }
                }
            }
            return 0.0;
        }

        [DllExport]
        public static IntPtr GetString(IntPtr data)
        {
            Measure measure = (Measure)data;

            if (Counters.counters.TryGetValue(measure.myCatagories.Catagory + "|" + measure.myCatagories.SubCatagory, out Counters.CounterLists counters))
            {
                if (counters.ByUsage.Count > measure.myInstance && measure.myInstance >= 0)
                {
                    return Marshal.StringToHGlobalUni(counters.ByUsage[measure.myInstance].Name);
                }
                else
                {
                    if (counters.ByName.TryGetValue(measure.myName, out Counter counter))
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
        //    [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        //{
        //    Measure measure = (Measure)data;
        //
        //    return Marshal.StringToHGlobalUni(""); //returning IntPtr.Zero will result in it not being used
        //}
    }
}
