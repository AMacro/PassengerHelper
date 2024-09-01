﻿namespace PassengerHelperPlugin.Support;

using System.Collections.Generic;

public class PassengerLocomotiveSettings
{
    public bool StopForDiesel { get; set; } = false;
    public float DieselLevel { get; set; } = 0.10f;
    public bool StopForCoal { get; set; } = false;
    public float CoalLevel { get; set; } = 0.10f;
    public bool StopForWater { get; set; } = false;
    public float WaterLevel { get; set; } = 0.10f;
    public bool StopAtNextStation { get; set; } = false;
    public bool StopAtLastStation { get; set; } = false;
    public bool PointToPointMode { get; set; } = true;
    public bool LoopMode { get; set; } = false;
    public bool WaitForFullPassengersLastStation { get; set; } = false;
    public bool Disable { get; set; } = false;
    public DirectionOfTravel DirectionOfTravel { get; set; } = DirectionOfTravel.UNKNOWN;
    public bool DoTLocked { get; set; } = false;
    public bool gameLoadFlag { get; set; } = false;

    // settings to save current status of train for next game load
    public TrainStatus TrainStatus { get; set; } = new TrainStatus();


    public SortedDictionary<string, StationSetting> Stations { get; } = new() {
            { "sylva", new StationSetting() },
            { "dillsboro", new StationSetting() },
            { "wilmot", new StationSetting() },
            { "whittier", new StationSetting() },
            { "ela", new StationSetting() },
            { "bryson", new StationSetting() },
            { "hemingway", new StationSetting() },
            { "alarkajct", new StationSetting() },
            { "cochran", new StationSetting() },
            { "alarka", new StationSetting() },
            { "almond", new StationSetting() },
            { "nantahala", new StationSetting() },
            { "topton", new StationSetting() },
            { "rhodo", new StationSetting() },
            { "andrews", new StationSetting() }
        };

    internal int getSettingsHash()
    {
        int prime = 31;
        int result = 1;
        result = prime * result + StopForDiesel.GetHashCode();
        result = prime * result + DieselLevel.GetHashCode();

        result = prime * result + StopForCoal.GetHashCode();
        result = prime * result + CoalLevel.GetHashCode();

        result = prime * result + StopForWater.GetHashCode();
        result = prime * result + WaterLevel.GetHashCode();

        result = prime * result + StopAtNextStation.GetHashCode();
        result = prime * result + StopAtLastStation.GetHashCode();

        result = prime * result + PointToPointMode.GetHashCode();
        result = prime * result + LoopMode.GetHashCode();

        result = prime * result + WaitForFullPassengersLastStation.GetHashCode();

        result = prime * result + Disable.GetHashCode();

        result = prime * result + DirectionOfTravel.GetHashCode();
        result = prime * result + DoTLocked.GetHashCode();

        result = prime * result + gameLoadFlag.GetHashCode();

        result = prime * result + TrainStatus.PreviousStation.GetHashCode();
        result = prime * result + TrainStatus.CurrentStation.GetHashCode();
        result = prime * result + Stations.GetHashCode();

        return result;
    }
}

public class StationSetting
{
    public bool StopAt { get; set; } = false;
    public bool TerminusStation { get; set; } = false;
    public bool PickupPassengers { get; set; } = false;
    public bool Pause { get; set; } = false;
    public bool Transfer { get; set; } = false;
    public PassengerMode PassengerMode { get; set; } = PassengerMode.Normal;

    public override string ToString()
    {
        return "StationSetting[ StopAt=" + StopAt + ", TerminusStation=" + TerminusStation + ", PickupPassengers=" + PickupPassengers + ", Pause=" + Pause + ", Transfer=" + Transfer + "PassengerMode=" + PassengerMode + "]";
    }
}

public enum PassengerMode
{
    Normal,
    Pause,
    Transfer
}

public enum DirectionOfTravel
{
    EAST,
    UNKNOWN,
    WEST
}

public class TrainStatus
{
    public string PreviousStation { get; set; } = "";
    public string CurrentStation { get; set; } = "";
    public bool Arrived { get; set; } = false;
    public bool AtTerminusStationEast { get; set; } = false;
    public bool AtTerminusStationWest { get; set; } = false;
    public bool AtAlarka { get; set; } = false;
    public bool AtCochran { get; set; } = false;
    public bool TerminusStationProcedureComplete { get; set; } = false;
    public bool NonTerminusStationProcedureComplete { get; set; } = false;
    public bool CurrentlyStopped { get; set; } = false;
    public string CurrentReasonForStop { get; set; } = "";
    public bool StoppedForDiesel { get; set; } = false;
    public bool StoppedForCoal { get; set; } = false;
    public bool StoppedForWater { get; set; } = false;
    public bool StoppedNextStation { get; set; } = false;
    public bool StoppedTerminusStation { get; set; } = false;
    public bool StoppedStationPause { get; set; } = false;
    public bool StoppedWaitForFullLoad { get; set; } = false;
    public bool ReadyToDepart { get; set; } = false;
    public bool Departed { get; set; } = false;
    public bool Continue { get; set; } = false;

    public void ResetStoppedFlags()
    {
        CurrentlyStopped = false;
        CurrentReasonForStop = "";
        StoppedForDiesel = false;
        StoppedForCoal = false;
        StoppedForWater = false;
        StoppedNextStation = false;
        StoppedTerminusStation = false;
        StoppedStationPause = false;
        StoppedWaitForFullLoad = false;
    }

    public void ResetStatusFlags()
    {
        ResetStoppedFlags();
        Arrived = false;
        AtTerminusStationEast = false;
        AtTerminusStationWest = false;
        AtAlarka = false;
        AtCochran = false;
        TerminusStationProcedureComplete = false;
        NonTerminusStationProcedureComplete = false;
        ReadyToDepart = false;
        Departed = false;
        Continue = false;
    }
}