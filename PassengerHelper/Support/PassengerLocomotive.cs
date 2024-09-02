namespace PassengerHelperPlugin.Support;

using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using Game.Messages;
using Game.Notices;
using Game.State;
using KeyValue.Runtime;
using Model;
using Model.AI;
using Model.Definition;
using Model.Definition.Data;
using Model.OpsNew;
using RollingStock;
using Serilog;
using UI.EngineControls;
using static Model.Car;


public class PassengerLocomotive
{
    readonly ILogger logger = Log.ForContext(typeof(PassengerLocomotive));

    internal readonly BaseLocomotive _locomotive;

    public PassengerLocomotiveSettings Settings;

    private PassengerStop? _currentStop = null;
    private PassengerStop? _previousStop = null;
    public PassengerStop? CurrentStation
    {
        get
        {
            return _currentStop;
        }
        set
        {
            _currentStop = value;
            if (value != null)
            {
                TrainStatus.CurrentStation = value.identifier;
            }
            else
            {
                TrainStatus.CurrentStation = "";
            }
        }
    }
    public PassengerStop? PreviousStation
    {
        get
        {
            return _previousStop;
        }
        set
        {
            _previousStop = value;
            if (value != null)
            {
                TrainStatus.PreviousStation = value.identifier;
            }
            else
            {
                TrainStatus.PreviousStation = "";
            }
        }
    }
    internal TrainStatus TrainStatus;

    private readonly bool hasTender = false;
    private Car FuelCar;
    private int _dieselFuelSlotIndex;
    private float _dieselSlotMax;
    private int _coalSlotIndex;
    private float _coalSlotMax;
    private int _waterSlotIndex;
    private float _waterSlotMax;

    private Orders? cachedOrders = null;

    internal int settingsHash = 0;
    internal int stationSettingsHash = 0;

    private bool _selfSentOrders = false;

    public PassengerLocomotive(BaseLocomotive _locomotive, PassengerLocomotiveSettings Settings)
    {
        this._locomotive = _locomotive;
        if (_locomotive.Archetype == CarArchetype.LocomotiveSteam)
        {
            hasTender = true;
        }

        this.Settings = Settings;

        AutoEngineerPersistence persistence = new(_locomotive.KeyValueObject);
        AutoEngineerOrdersHelper helper = new(_locomotive, persistence);

        persistence.ObserveOrders(delegate (Orders orders)
        {
            logger.Information("Orders changed. Orders are now: {0} and selfSentOrders is: {1}", orders, _selfSentOrders);
            if (!_selfSentOrders)
            {
                // if it is the start up of the game, the game sends an updated order to get the train moving again, so ignore it
                if (Settings.gameLoadFlag)
                {
                    Settings.gameLoadFlag = false;
                    return;
                }

                // if we aren't locked, we shouldn't change to unknown
                if (!Settings.DoTLocked)
                {
                    return;
                }

                Settings.DirectionOfTravel = DirectionOfTravel.UNKNOWN;
                Settings.DoTLocked = false;
            }
            _selfSentOrders = false;
        });

        LoadSettings(Settings);
        this.FuelCar = GetFuelCar();
    }

    private void LoadSettings(PassengerLocomotiveSettings Settings)
    {
        this.TrainStatus = Settings.TrainStatus;

        IEnumerable<PassengerStop> stations = PassengerStop.FindAll();

        if (TrainStatus.CurrentStation.Length > 0)
        {
            this.CurrentStation = stations.FirstOrDefault((PassengerStop stop) => stop.identifier == TrainStatus.CurrentStation);
        }

        if (TrainStatus.PreviousStation.Length > 0)
        {
            this.PreviousStation = stations.FirstOrDefault((PassengerStop stop) => stop.identifier == TrainStatus.PreviousStation);
        }

        if ((this.TrainStatus.Arrived || this.TrainStatus.ReadyToDepart) && this.CurrentStation != null)
        {
            logger.Information("Train did not depart yet, selecting current station on passenger cars");
            _locomotive.velocity = 0;
            IEnumerable<Car> coaches = _locomotive.EnumerateCoupled().Where(car => car.Archetype == CarArchetype.Coach);

            foreach (Car coach in coaches)
            {
                PassengerMarker marker = coach.GetPassengerMarker() ?? new PassengerMarker();

                HashSet<string> destinations = marker.Destinations;

                destinations.Add(TrainStatus.CurrentStation);
                StateManager.ApplyLocal(new SetPassengerDestinations(coach.id, destinations.ToList()));
            }
        }

        if (this.TrainStatus.Departed && this.CurrentStation == null && this.PreviousStation != null)
        {
            logger.Information("Train is not at a station, but is in route, re-selecting stations to be safe");
            IEnumerable<Car> coaches = _locomotive.EnumerateCoupled().Where(car => car.Archetype == CarArchetype.Coach);

            foreach (Car coach in coaches)
            {
                PassengerMarker marker = coach.GetPassengerMarker() ?? new PassengerMarker();

                HashSet<string> destinations = marker.Destinations;

                StateManager.ApplyLocal(new SetPassengerDestinations(coach.id, destinations.ToList()));
            }
        }

        if (Settings.DirectionOfTravel == DirectionOfTravel.UNKNOWN)
        {
            Settings.DoTLocked = false;
        }

    }

    private float GetDieselLevelForLoco()
    {
        float level = 0f;
        CarLoadInfo? loadInfo = FuelCar.GetLoadInfo(_dieselFuelSlotIndex);
        if (loadInfo.HasValue && _locomotive.Archetype == CarArchetype.LocomotiveDiesel)
        {
            logger.Information("{0} has {1}gal of diesel fuel", _locomotive.DisplayName, loadInfo.Value.Quantity);
            level = loadInfo.Value.Quantity;
        }

        return level;
    }

    public bool CheckDieselFuelLevel(out float level)
    {
        level = GetDieselLevelForLoco();
        float minLevel = Settings.DieselLevel;
        float actualLevel = level / _dieselSlotMax;
        logger.Information("diesel: min level is: {0}, actual level is: {1}, max quantity is: {2}", minLevel, actualLevel, _dieselSlotMax);

        TrainStatus.StoppedForDiesel = _locomotive.Archetype == CarArchetype.LocomotiveDiesel && actualLevel < minLevel;

        if (TrainStatus.StoppedForDiesel)
        {
            logger.Information("{0} is low on diesel", _locomotive.DisplayName);
            TrainStatus.CurrentReasonForStop = "stopped for low diesel";
            TrainStatus.CurrentlyStopped = true;
        }
        return TrainStatus.StoppedForDiesel;
    }

    private float GetCoalLevelForLoco()
    {
        float level = 0f;
        CarLoadInfo? loadInfo = FuelCar.GetLoadInfo(_coalSlotIndex);
        if (loadInfo.HasValue && _locomotive.Archetype == CarArchetype.LocomotiveSteam)
        {
            logger.Information("{0} has {1}T of coal", _locomotive.DisplayName, loadInfo.Value.Quantity / 2000);
            level = loadInfo.Value.Quantity;
        }

        return level;
    }

    public bool CheckCoalLevel(out float level)
    {
        level = GetCoalLevelForLoco();
        float minLevel = Settings.CoalLevel;
        float actualLevel = level / _coalSlotMax;
        logger.Information("coal: min level is: {0}, actual level is: {1}, max quantity is: {2}", minLevel, actualLevel, _coalSlotMax);

        TrainStatus.StoppedForCoal = hasTender && actualLevel < minLevel;

        if (TrainStatus.StoppedForCoal)
        {
            logger.Information("{0} is low on coal", _locomotive.DisplayName);
            TrainStatus.CurrentReasonForStop = "stopped for low coal";
            TrainStatus.CurrentlyStopped = true;
        }
        return TrainStatus.StoppedForCoal;
    }

    private float GetWaterLevelForLoco()
    {
        float level = 0f;
        CarLoadInfo? loadInfo = FuelCar.GetLoadInfo(_waterSlotIndex);
        if (loadInfo.HasValue && _locomotive.Archetype == CarArchetype.LocomotiveSteam)
        {
            logger.Information("{0} has {1}gal of water", _locomotive.DisplayName, loadInfo.Value.Quantity);
            level = loadInfo.Value.Quantity;
        }

        return level;
    }

    public bool CheckWaterLevel(out float level)
    {
        level = GetWaterLevelForLoco();
        float minLevel = Settings.WaterLevel;
        float actualLevel = level / _waterSlotMax;
        logger.Information("water: min level is: {0}, actual level is: {1}, max quantity is: {2}", minLevel, actualLevel, _waterSlotMax);

        TrainStatus.StoppedForWater = hasTender && actualLevel < minLevel;

        if (TrainStatus.StoppedForWater)
        {
            logger.Information("{0} is low on water", _locomotive.DisplayName);
            TrainStatus.CurrentReasonForStop = "stopped for low water";
            TrainStatus.CurrentlyStopped = true;
        }
        return TrainStatus.StoppedForWater;
    }

    public void ResetStoppedFlags()
    {
        logger.Information("resetting Stop flags for {0}", _locomotive.DisplayName);
        TrainStatus.ResetStoppedFlags();
    }

    public void ResetStatusFlags()
    {
        logger.Information("resetting Status flags for {0}", _locomotive.DisplayName);
        TrainStatus.ResetStatusFlags();
    }

    public bool ShouldStayStopped()
    {
        logger.Information("checking if {0} should stay Stopped at current station", _locomotive.DisplayName);
        AutoEngineerPersistence persistence = new(_locomotive.KeyValueObject);
        AutoEngineerOrdersHelper helper = new(_locomotive, persistence);

        if (cachedOrders == null)
        {
            cachedOrders = persistence.Orders;
        }

        if (TrainStatus.Continue)
        {
            logger.Information("Continue button clicked. Continuing", _locomotive.DisplayName);
            TrainStatus.CurrentlyStopped = false;

            _selfSentOrders = true;
            logger.Information("Cached orders are: ", cachedOrders);
            helper.SetOrdersValue(cachedOrders?.Mode(), cachedOrders?.Forward, cachedOrders?.MaxSpeedMph);
            cachedOrders = null;

            return false;
        }

        bool stayStopped = false;

        // train was requested to remain stopped
        if (Settings.StopAtNextStation)
        {
            logger.Information("StopAtNextStation is selected. {0} is remaining stopped.", _locomotive.DisplayName);
            stayStopped = true;
        }

        if (Settings.StopAtTerminusStation && Settings.StationSettings[CurrentStation.identifier].TerminusStation == true)
        {
            logger.Information("StopAtLastStation are selected. {0} is remaining stopped.", _locomotive.DisplayName);
            stayStopped = true;
        }

        if (Settings.StationSettings[CurrentStation.identifier].PauseAtStation)
        {
            logger.Information("Requested Pause at this station. {0} is remaining stopped.", _locomotive.DisplayName);
            stayStopped = true;
        }

        if (Settings.DirectionOfTravel == DirectionOfTravel.UNKNOWN)
        {
            logger.Information("Direction of Travel is still unknown. {0} is remaining stopped.", _locomotive.DisplayName);
            stayStopped = true;
        }

        // train is stopped because of low diesel, coal or water
        if (TrainStatus.StoppedForDiesel || TrainStatus.StoppedForCoal || TrainStatus.StoppedForWater)
        {
            logger.Information("Locomotive is stopped due to either low diesel, coal or water. Rechecking settings to see if they have changed.");
            // first check if the setting has been set to false
            if (!Settings.StopForDiesel && TrainStatus.StoppedForDiesel)
            {
                logger.Information("StopForDiesel no longer selected, resetting flag.");
                TrainStatus.StoppedForDiesel = false;
            }

            if (!Settings.StopForCoal && TrainStatus.StoppedForCoal)
            {
                logger.Information("StopForCoal no longer selected, resetting flag.");
                TrainStatus.StoppedForCoal = false;
            }

            if (!Settings.StopForWater && TrainStatus.StoppedForWater)
            {
                logger.Information("StopForWater no longer selected, resetting flag.");
                TrainStatus.StoppedForWater = false;
            }

            stayStopped = TrainStatus.StoppedForDiesel || TrainStatus.StoppedForCoal || TrainStatus.StoppedForWater;
        }

        if (stayStopped)
        {
            if (Settings.DirectionOfTravel != DirectionOfTravel.UNKNOWN)
            {
                persistence.PassengerModeStatus = "Paused";
            }

            _selfSentOrders = true;
            helper.SetOrdersValue(cachedOrders?.Mode(), cachedOrders?.Forward, 0);
        }
        else
        {
            _selfSentOrders = true;
            helper.SetOrdersValue(cachedOrders?.Mode(), cachedOrders?.Forward, cachedOrders?.MaxSpeedMph);
            cachedOrders = null;
        }

        return stayStopped;
    }

    public void ReverseLocoDirection()
    {
        _selfSentOrders = true;
        logger.Information("reversing loco direction");
        AutoEngineerPersistence persistence = new(_locomotive.KeyValueObject);
        AutoEngineerOrdersHelper helper = new(_locomotive, persistence);
        logger.Information("Current direction is {0}", persistence.Orders.Forward == true ? "forward" : "backward");
        helper.SetOrdersValue(null, !persistence.Orders.Forward);
        logger.Information("new direction is {0}", persistence.Orders.Forward == true ? "forward" : "backward");
    }

    public void PostNotice(string key, string message)
    {
        _locomotive.PostNotice(key, message);
    }

    public void ResetSettingsHash()
    {
        this.settingsHash = 0;
        this.stationSettingsHash = 0;
    }

    private Car GetFuelCar()
    {
        if (!hasTender)
        {
            _dieselFuelSlotIndex = _locomotive.Definition.LoadSlots.FindIndex((LoadSlot loadSlot) => loadSlot.RequiredLoadIdentifier == "diesel-fuel");
            _dieselSlotMax = _locomotive.Definition.LoadSlots.Where((LoadSlot loadSlot) => loadSlot.RequiredLoadIdentifier == "diesel-fuel").First().MaximumCapacity;

            return _locomotive;
        }

        if (TryGetTender(out var tender))
        {
            _coalSlotIndex = tender.Definition.LoadSlots.FindIndex((LoadSlot loadSlot) => loadSlot.RequiredLoadIdentifier == "coal");
            _coalSlotMax = tender.Definition.LoadSlots.Where((LoadSlot loadSlot) => loadSlot.RequiredLoadIdentifier == "coal").First().MaximumCapacity;

            _waterSlotIndex = tender.Definition.LoadSlots.FindIndex((LoadSlot loadSlot) => loadSlot.RequiredLoadIdentifier == "water");
            _waterSlotMax = tender.Definition.LoadSlots.Where((LoadSlot loadSlot) => loadSlot.RequiredLoadIdentifier == "water").First().MaximumCapacity;

            return tender;
        }

        throw new Exception("steam engine with no tender. How????");
    }

    private bool TryGetTender(out Car tender)
    {
        if (hasTender && _locomotive.TryGetAdjacentCar(_locomotive.EndToLogical(End.R), out tender) && tender.Archetype == CarArchetype.Tender)
        {
            return true;
        }

        throw new Exception("steam engine with no tender. How????");
    }


}
