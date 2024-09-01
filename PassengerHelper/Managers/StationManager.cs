namespace PassengerHelperPlugin.Managers;

using System.Collections.Generic;
using System.Linq;
using Game;
using Game.Messages;
using Game.State;
using Support;
using Model;
using Model.Definition;
using Model.Definition.Data;
using Model.OpsNew;
using Network;
using RollingStock;
using Serilog;
using UnityEngine;
using System.Collections;
using GameObjects;

public class StationManager
{
    static readonly Serilog.ILogger logger = Log.ForContext(typeof(StationManager));

    internal readonly List<string> orderedStations;

    internal TrainManager trainManager;
    internal SettingsManager settingsManager;

    public StationManager(SettingsManager settingsManager, TrainManager trainManager, List<string> orderedStations)
    {
        this.orderedStations = orderedStations;
        this.trainManager = trainManager;
        this.settingsManager = settingsManager;
    }

    internal List<PassengerStop> GetPassengerStops()
    {
        return PassengerStop.FindAll()
        .Where(ps => !ps.ProgressionDisabled)
        .OrderBy(d => orderedStations.IndexOf(d.identifier))
        .ToList();
    }

    public bool HandleTrainAtStation(BaseLocomotive locomotive, PassengerStop currentStop)
    {
        logger.Information("HandleTrainAtStation");
        PassengerLocomotive passengerLocomotive = this.trainManager.GetPassengerLocomotive(locomotive);
        PassengerLocomotiveSettings settings = passengerLocomotive.Settings;
        logger.Information("HandleTrainAtStation:: got locomotive and settings: {0}", settings.ToString());

        if (settings.Disable)
        {
            logger.Information("Passenger Helper is currently disabled for {0} due to disabled setting.", locomotive.DisplayName);
            // return to original game logic
            return false;
        }

        if (currentStop != passengerLocomotive.CurrentStation)
        {
            logger.Information("Train {0} has arrived at station {1}", locomotive.DisplayName, currentStop.DisplayName);
            passengerLocomotive.CurrentStation = currentStop;
            passengerLocomotive.ResetSettingsHash();
            passengerLocomotive.ResetStatusFlags();

            passengerLocomotive.TrainStatus.Arrived = true;
        }

        // if we have departed, cease all procedures unless a setting was changed after running the procedure
        if (passengerLocomotive.TrainStatus.ReadyToDepart && passengerLocomotive.settingsHash == settings.getSettingsHash() + passengerLocomotive.CurrentStation.identifier.GetHashCode())
        {
            return false;
        }

        // v2
        // is next station occupied?
        // is train en-route to current station?
        // how many tracks does current station have?
        // route appropriately

        // if train is currently Stopped
        if (IsStoppedAndShouldStayStopped(passengerLocomotive))
        {
            return true;
        }

        if (passengerLocomotive.stationSettingsHash != settings.Stations.GetHashCode() + passengerLocomotive.CurrentStation.identifier.GetHashCode())
        {
            if (!passengerLocomotive.TrainStatus.Continue)
            {
                logger.Information("Station settings have changed, running Station Procedure");
                if (RunStationProcedure(passengerLocomotive, settings))
                {
                    return true;
                }
            }
        }
        else
        {
            logger.Information("Station Settings have not changed. Skipping Station Procedure");
        }

        if (passengerLocomotive.settingsHash != settings.getSettingsHash() + passengerLocomotive.CurrentStation.identifier.GetHashCode())
        {
            if (!passengerLocomotive.TrainStatus.Continue)
            {
                logger.Information("Passenger settings have changed, checking for pause conditions");
                passengerLocomotive.ResetStoppedFlags();

                if (PauseAtCurrentStation(passengerLocomotive, settings))
                {
                    return true;
                }

                if (HaveLowFuel(passengerLocomotive, settings))
                {
                    return true;
                }
            }

            passengerLocomotive.settingsHash = settings.getSettingsHash() + passengerLocomotive.CurrentStation.identifier.GetHashCode();
        }
        else
        {
            logger.Information("Passenger settings have not changed, skipping check for pause conditions");
        }

        if (!passengerLocomotive.TrainStatus.CurrentlyStopped)
        {
            logger.Information("Train {0} is ready to depart station {1}", locomotive.DisplayName, currentStop.DisplayName);
            passengerLocomotive.TrainStatus.ReadyToDepart = true;
        }

        return passengerLocomotive.TrainStatus.CurrentlyStopped;
    }

    private bool IsStoppedAndShouldStayStopped(PassengerLocomotive passengerLocomotive)
    {
        if (passengerLocomotive.TrainStatus.CurrentlyStopped)
        {
            logger.Information("Train is currently Stopped due to: {0}", passengerLocomotive.TrainStatus.CurrentReasonForStop);
            if (passengerLocomotive.ShouldStayStopped())
            {
                return true;
            }
        }

        return false;
    }

    private bool PauseAtCurrentStation(PassengerLocomotive passengerLocomotive, PassengerLocomotiveSettings settings)
    {
        if (settings.StopAtNextStation)
        {
            logger.Information("Pausing at station due to setting");
            passengerLocomotive.PostNotice("ai-stop", $"Paused at {Hyperlink.To(passengerLocomotive.CurrentStation)}.");
            passengerLocomotive.TrainStatus.CurrentlyStopped = true;
            passengerLocomotive.TrainStatus.CurrentReasonForStop = "Requested pause at next station";
            return true;
        }

        if (settings.Stations[passengerLocomotive.CurrentStation.identifier].PassengerMode == PassengerMode.Pause)
        {
            logger.Information("Pausing at {0} due to setting", passengerLocomotive.CurrentStation.DisplayName);
            passengerLocomotive.PostNotice("ai-stop", $"Paused at {Hyperlink.To(passengerLocomotive.CurrentStation)}.");
            passengerLocomotive.TrainStatus.CurrentlyStopped = true;
            passengerLocomotive.TrainStatus.CurrentReasonForStop = "Requested pause at " + passengerLocomotive.CurrentStation.DisplayName;
            return true;
        }

        if (settings.StopAtLastStation && settings.Stations[passengerLocomotive.CurrentStation.identifier].TerminusStation == true)
        {
            logger.Information("Pausing at {0} due to setting", passengerLocomotive.CurrentStation.DisplayName);
            passengerLocomotive.PostNotice("ai-stop", $"Paused at terminus station {Hyperlink.To(passengerLocomotive.CurrentStation)}.");
            passengerLocomotive.TrainStatus.CurrentlyStopped = true;
            passengerLocomotive.TrainStatus.CurrentReasonForStop = "Requested pause at terminus station " + passengerLocomotive.CurrentStation.DisplayName;
            return true;
        }

        return false;
    }

    private bool HaveLowFuel(PassengerLocomotive passengerLocomotive, PassengerLocomotiveSettings settings)
    {
        bool retVal = false;
        if (settings.StopForDiesel)
        {
            logger.Information("Requested stop for low diesel, checking level");
            // check diesel
            if (passengerLocomotive.CheckDieselFuelLevel(out float diesel))
            {
                passengerLocomotive.PostNotice("ai-stop", $"Stopped, low diesel at {Hyperlink.To(passengerLocomotive.CurrentStation)}.");
                retVal = true;
            }
        }

        if (settings.StopForCoal)
        {
            logger.Information("Requested stop for low coal, checking level");
            // check coal
            if (passengerLocomotive.CheckCoalLevel(out float coal))
            {
                passengerLocomotive.PostNotice("ai-stop", $"Stopped, low coal at {Hyperlink.To(passengerLocomotive.CurrentStation)}.");
                retVal = true;
            }
        }

        if (settings.StopForWater)
        {
            logger.Information("Requested stop for low water, checking level");
            // check water
            if (passengerLocomotive.CheckWaterLevel(out float water))
            {
                passengerLocomotive.PostNotice("ai-stop", $"Stopped, low water at {Hyperlink.To(passengerLocomotive.CurrentStation)}.");
                retVal = true;
            }
        }

        return retVal;
    }

    private bool RunStationProcedure(PassengerLocomotive passengerLocomotive, PassengerLocomotiveSettings settings)
    {
        BaseLocomotive _locomotive = passengerLocomotive._locomotive;
        string LocomotiveName = _locomotive.DisplayName;
        PassengerStop CurrentStop = passengerLocomotive.CurrentStation;
        string CurrentStopIdentifier = CurrentStop.identifier;
        string CurrentStopName = CurrentStop.DisplayName;
        IEnumerable<Car> coaches = _locomotive.EnumerateCoupled().Where(car => car.Archetype == CarArchetype.Coach);
        List<string> orderedTerminusStations = settings.Stations.Where(station => station.Value.TerminusStation == true).Select(station => station.Key).OrderBy(d => orderedStations.IndexOf(d)).ToList();
        List<string> orderedSelectedStations = settings.Stations.Where(station => station.Value.StopAt == true).Select(station => station.Key).OrderBy(d => orderedStations.IndexOf(d)).ToList();


        logger.Information("Running station procedure for Train {0} at {1} with {2} coaches, the following selected stations: {3}, and the following terminus stations: {4}, in the following direction: {5}",
            LocomotiveName, CurrentStopName, coaches.Count(), orderedSelectedStations, orderedTerminusStations, settings.DirectionOfTravel.ToString()
        );

        if (orderedTerminusStations.Count != 2)
        {
            logger.Information("there are not exactly 2 terminus stations, current selected terminus stations: {0}", orderedTerminusStations);
            Say($"AI Engineer {Hyperlink.To(_locomotive)}: \"2 Terminus stations must be selected. Check your passenger settings.\"");

            passengerLocomotive.TrainStatus.CurrentlyStopped = true;
            passengerLocomotive.TrainStatus.CurrentReasonForStop = "Terminus stations not selected";
            return true;
        }

        int indexEastTerminus = orderedSelectedStations.IndexOf(orderedTerminusStations[0]);
        int indexWestTerminus = orderedSelectedStations.IndexOf((orderedTerminusStations[1]));

        if (!orderedTerminusStations.Contains(CurrentStopIdentifier))
        {
            logger.Information("Not at a terminus station");

            if (settings.DirectionOfTravel == DirectionOfTravel.UNKNOWN && (passengerLocomotive.PreviousStation == null || passengerLocomotive.PreviousStation == CurrentStop))
            {
                string reason = "Unknown Direction of Travel";
                if (passengerLocomotive.TrainStatus.CurrentReasonForStop != reason)
                {
                    logger.Information("Train is in {0}, previous stop is not known, direction of travel is unknown, so cannot accurately determine direction, pausing and waiting for manual intervention", CurrentStop.DisplayName);
                    Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Unknown Direction. Pausing at {Hyperlink.To(CurrentStop)} until I receive Direction of Travel via PassengerSettings.\"");
                    Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Be sure to put the reverser in the correct direction too. Else I might go in the wrong direction.\"");
                    passengerLocomotive.PostNotice("ai-stop", $"Paused, Unknown Direction at {Hyperlink.To(CurrentStop)}.");
                    passengerLocomotive.TrainStatus.CurrentlyStopped = true;
                    passengerLocomotive.TrainStatus.CurrentReasonForStop = reason;

                    if (CurrentStop.identifier == "alarka")
                    {
                        passengerLocomotive.TrainStatus.AtAlarka = true;
                    }

                    if (CurrentStop.identifier == "cochran")
                    {
                        passengerLocomotive.TrainStatus.AtCochran = true;
                    }
                }

                return true;
            }

            bool nonTerminusStationProcedureRetVal = RunNonTerminusStationProcedure(passengerLocomotive, settings, CurrentStop, coaches, orderedSelectedStations, orderedTerminusStations);

            if (!nonTerminusStationProcedureRetVal)
            {
                logger.Information("Setting Previous stop to the current stop (not at terminus)");
                passengerLocomotive.PreviousStation = CurrentStop;

                settings.DoTLocked = true;
                passengerLocomotive.TrainStatus.NonTerminusStationProcedureComplete = true;
                settingsManager.SaveSettings(LocomotiveName, passengerLocomotive.TrainStatus);
            }

            // setting the previous stop on the settings changes the hash, so re-cache the settings
            passengerLocomotive.stationSettingsHash = settings.Stations.GetHashCode() + passengerLocomotive.CurrentStation.identifier.GetHashCode();
            passengerLocomotive.settingsHash = settings.getSettingsHash();

            return nonTerminusStationProcedureRetVal;
        }
        else
        {
            logger.Information("At terminus station");

            bool atTerminusStationWest = orderedTerminusStations[1] == CurrentStopIdentifier;
            bool atTerminusStationEast = orderedTerminusStations[0] == CurrentStopIdentifier;

            logger.Information("at west terminus: {0} at east terminus {1}", atTerminusStationWest, atTerminusStationEast);
            logger.Information("passenger locomotive atTerminusWest settings: {0}", passengerLocomotive.TrainStatus.AtTerminusStationWest);
            logger.Information("passenger locomotive atTerminusEast settings: {0}", passengerLocomotive.TrainStatus.AtTerminusStationEast);

            // true means stay stopped, false means continue
            bool terminusStationProcedureRetVal = true;

            if (atTerminusStationWest && !passengerLocomotive.TrainStatus.AtTerminusStationWest)
            {
                terminusStationProcedureRetVal = RunTerminusStationProcedure(passengerLocomotive, settings, CurrentStop, coaches, orderedSelectedStations, orderedTerminusStations, DirectionOfTravel.EAST);

                logger.Information("Setting Previous stop to the current stop (west terminus)");
            }

            if (atTerminusStationEast && !passengerLocomotive.TrainStatus.AtTerminusStationEast)
            {
                terminusStationProcedureRetVal = RunTerminusStationProcedure(passengerLocomotive, settings, CurrentStop, coaches, orderedSelectedStations, orderedTerminusStations, DirectionOfTravel.WEST);

                logger.Information("Setting Previous stop to the current stop (east terminus)");
            }

            if ((passengerLocomotive.TrainStatus.AtTerminusStationWest || passengerLocomotive.TrainStatus.AtTerminusStationEast) && settings.WaitForFullPassengersLastStation)
            {
                logger.Information("Waiting For full Passengers at terminus.");

                foreach (Car coach in coaches)
                {
                    PassengerMarker? marker = coach.GetPassengerMarker();


                    if (marker != null && marker.HasValue)
                    {
                        LoadSlot loadSlot = coach.Definition.LoadSlots.FirstOrDefault((LoadSlot slot) => slot.RequiredLoadIdentifier == "passengers");
                        int maxCapacity = (int)loadSlot.MaximumCapacity;
                        PassengerMarker actualMarker = marker.Value;
                        if (actualMarker.TotalPassengers < maxCapacity)
                        {
                            logger.Information("Passenger car not full, remaining stopped");
                            passengerLocomotive.TrainStatus.CurrentlyStopped = true;
                            passengerLocomotive.TrainStatus.CurrentReasonForStop = "Waiting for full passengers at terminus station";

                            return true;
                        }
                    }
                }

                logger.Information("Passengers are full, continuing.");
            }

            if (!terminusStationProcedureRetVal)
            {
                passengerLocomotive.PreviousStation = CurrentStop;
                passengerLocomotive.TrainStatus.AtTerminusStationWest = atTerminusStationWest;
                passengerLocomotive.TrainStatus.AtTerminusStationEast = atTerminusStationEast;
                passengerLocomotive.TrainStatus.TerminusStationProcedureComplete = true;

                settings.DoTLocked = true;

                settingsManager.SaveSettings(LocomotiveName, passengerLocomotive.TrainStatus);
            }


            // setting the previous stop on the settings changes the hash, so re-cache the settings
            passengerLocomotive.stationSettingsHash = settings.Stations.GetHashCode() + passengerLocomotive.CurrentStation.identifier.GetHashCode();
            passengerLocomotive.settingsHash = settings.getSettingsHash();

            return terminusStationProcedureRetVal;
        }

    }

    private bool RunNonTerminusStationProcedure(PassengerLocomotive passengerLocomotive, PassengerLocomotiveSettings settings, PassengerStop CurrentStop, IEnumerable<Car> coaches, List<string> orderedStopAtStations, List<string> orderedTerminusStations)
    {
        string currentStopIdentifier = CurrentStop.identifier;
        string prevStopIdentifier = passengerLocomotive.PreviousStation != null ? passengerLocomotive.PreviousStation.identifier : "";

        passengerLocomotive.TrainStatus.AtTerminusStationWest = false;
        passengerLocomotive.TrainStatus.AtTerminusStationEast = false;

        int westTerminusIndex = orderedStopAtStations.IndexOf(orderedTerminusStations[1]);
        int eastTerminusIndex = orderedStopAtStations.IndexOf(orderedTerminusStations[0]);
        int currentIndex = orderedStopAtStations.IndexOf(currentStopIdentifier);

        logger.Information("Not at either terminus station, so there are more stops");
        logger.Information("Checking to see if train is at station outside of terminus bounds");
        bool notAtASelectedStation = !orderedStopAtStations.Contains(currentStopIdentifier);

        if (notAtASelectedStation)
        {
            NotAtASelectedStationProcedure(passengerLocomotive, settings, CurrentStop, orderedStopAtStations, orderedTerminusStations);
        }

        string cochranIdentifier = "cochran";
        string alarkaIdentifier = "alarka";
        string almondIdentifier = "almond";

        logger.Information("Direction Intelligence: Current direction of travel: {0}, previousStop known: {1}, currentStop {2}",
                    settings.DirectionOfTravel,
                    passengerLocomotive.PreviousStation != null ? passengerLocomotive.PreviousStation.DisplayName : false,
                    CurrentStop.DisplayName);


        if (settings.DirectionOfTravel == DirectionOfTravel.UNKNOWN && passengerLocomotive.PreviousStation != null && passengerLocomotive.PreviousStation != CurrentStop)
        {
            logger.Information("Should now be able to determine which direction train is going");

            string reason = "Unknown Direction of Travel";
            if (currentStopIdentifier == cochranIdentifier && prevStopIdentifier == alarkaIdentifier && !orderedTerminusStations.Contains(alarkaIdentifier))
            {
                if (passengerLocomotive.TrainStatus.CurrentReasonForStop != reason)
                {
                    logger.Information("Train is in Cochran, previous stop was alarka, direction of travel is unknown, and alarka was not a terminus station, so cannot accurately determine direction, pausing and waiting for manual intervention");
                    Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Unknown Direction. Pausing at {Hyperlink.To(CurrentStop)} until I receive Direction of Travel via PassengerSettings.\"");
                    Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Be sure to put the reverser in the correct direction too. Else I might go in the wrong direction.\"");
                    passengerLocomotive.PostNotice("ai-stop", $"Paused, Unknown Direction at {Hyperlink.To(CurrentStop)}.");
                    passengerLocomotive.TrainStatus.CurrentlyStopped = true;
                    passengerLocomotive.TrainStatus.CurrentReasonForStop = reason;
                    passengerLocomotive.TrainStatus.AtCochran = true;
                }

                return true;
            }
            else if (currentStopIdentifier == alarkaIdentifier && prevStopIdentifier == cochranIdentifier)
            {
                if (passengerLocomotive.TrainStatus.CurrentReasonForStop != reason)
                {
                    logger.Information("Train is in Alarka, previous stop was cochran, direction of travel is unknown, so cannot accurately determine direction, pausing and waiting for manual intervention");
                    Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Unknown Direction. Pausing until at {Hyperlink.To(CurrentStop)} until I receive Direction of Travel via PassengerSettings.\"");
                    Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Be sure to put the reverser in the correct direction too. Else I might go in the wrong direction.\"");
                    passengerLocomotive.PostNotice("ai-stop", $"Paused, Unknown Direction at {Hyperlink.To(CurrentStop)}.");
                    passengerLocomotive.TrainStatus.CurrentlyStopped = true;
                    passengerLocomotive.TrainStatus.CurrentReasonForStop = reason;
                    passengerLocomotive.TrainStatus.AtAlarka = true;
                }

                return true;
            }
            else
            {
                logger.Information("Determining direction of travel");
                int indexPrev = orderedStopAtStations.IndexOf(passengerLocomotive.PreviousStation.identifier);

                if (indexPrev < currentIndex)
                {
                    logger.Information("Direction of Travel: WEST");
                    settings.DirectionOfTravel = DirectionOfTravel.WEST;
                }
                else
                {
                    logger.Information("Direction of Travel: EAST");
                    settings.DirectionOfTravel = DirectionOfTravel.EAST;
                }
            }
        }

        if (settings.DirectionOfTravel != DirectionOfTravel.UNKNOWN)
        {
            HashSet<string> expectedSelectedDestinations = new();
            if (settings.DirectionOfTravel == DirectionOfTravel.WEST)
            {
                // add one to range to include terminus station
                expectedSelectedDestinations = orderedStopAtStations.GetRange(currentIndex, westTerminusIndex - currentIndex + 1).ToHashSet();

                if (currentStopIdentifier == cochranIdentifier && prevStopIdentifier == alarkaIdentifier)
                {
                    logger.Information("Train is at cochran, heading west and alarka was previous station. Remove Alarka from expected stations");
                    expectedSelectedDestinations.Remove(alarkaIdentifier);
                }
            }

            if (settings.DirectionOfTravel == DirectionOfTravel.EAST)
            {
                // add one to range to include current station
                expectedSelectedDestinations = orderedStopAtStations.GetRange(0, currentIndex + 1).ToHashSet();

                if (currentStopIdentifier == cochranIdentifier && prevStopIdentifier == almondIdentifier)
                {
                    logger.Information("Train is at cochran, heading east and almond was previous station. Add Alarka to expected stations");
                    expectedSelectedDestinations.Add(alarkaIdentifier);
                }
            }

            if (currentStopIdentifier == alarkaIdentifier && orderedStopAtStations.Contains(cochranIdentifier))
            {
                logger.Information("Train is at alarka, heading {0} and alarka is not a terminus. Add cochran to expected stations", settings.DirectionOfTravel.ToString());
                expectedSelectedDestinations.Add(cochranIdentifier);
            }

            logger.Information("Expected selected stations are: {0}", expectedSelectedDestinations);

            // transfer station check
            int numTransferStations = settings.Stations.Where(s => s.Value.PassengerMode == PassengerMode.Transfer && s.Value.StopAt).Count();
            bool transferStationSelected = numTransferStations > 0;

            if (transferStationSelected)
            {
                logger.Information("Transfer station selected, checking direction and modifying expected selected stations");
                List<string> pickUpPassengerStations = settings.Stations.Where(s => s.Value.PickupPassengers).Select(s => s.Key).OrderBy(d => orderedStations.IndexOf(d)).ToList();
                int westTerminusIndex_Pickup = pickUpPassengerStations.IndexOf(orderedTerminusStations[1]);
                int eastTerminusIndex_Pickup = pickUpPassengerStations.IndexOf(orderedTerminusStations[0]);
                int currentIndex_Pickup = pickUpPassengerStations.IndexOf(currentStopIdentifier);
                logger.Information("The following stations are pickup stations: {0}", pickUpPassengerStations);

                if (settings.DirectionOfTravel == DirectionOfTravel.WEST)
                {
                    logger.Information("Selecting pickup stations {0} that are further west of the current station: {1}", pickUpPassengerStations.GetRange(currentIndex_Pickup, pickUpPassengerStations.Count - currentIndex_Pickup), orderedTerminusStations[1]);
                    // select all to the west of the current station
                    expectedSelectedDestinations.UnionWith(pickUpPassengerStations.GetRange(currentIndex_Pickup, pickUpPassengerStations.Count - currentIndex_Pickup));

                    if (currentStopIdentifier == "alarkajct" && orderedStopAtStations[currentIndex + 1] == almondIdentifier)
                    {
                        logger.Information("Train is at alarka jct station and next stop is almond station, deselecting cochran and alarka, if they are selected");
                        expectedSelectedDestinations.Remove(alarkaIdentifier);
                        expectedSelectedDestinations.Remove(cochranIdentifier);
                    }
                }

                if (settings.DirectionOfTravel == DirectionOfTravel.EAST)
                {
                    logger.Information("Selecting pickup stations {0} that are further east of the current station: {1}", pickUpPassengerStations.GetRange(0, currentIndex_Pickup + 1), orderedTerminusStations[0]);
                    // select all to the east of the current station
                    expectedSelectedDestinations.UnionWith(pickUpPassengerStations.GetRange(0, currentIndex_Pickup + 1));
                }
            }

            logger.Information("Checking passenger cars to make sure they have the proper selected stations");
            logger.Information("Setting the following stations: {0}", expectedSelectedDestinations);

            foreach (Car coach in coaches)
            {
                logger.Information("Checking Car {0}", coach.DisplayName);
                PassengerMarker? marker = coach.GetPassengerMarker();
                if (marker != null && marker.HasValue)
                {
                    PassengerMarker actualMarker = marker.Value;

                    logger.Information("Current selected stations are: {0}", actualMarker.Destinations);
                    if (!actualMarker.Destinations.SetEquals(expectedSelectedDestinations))
                    {
                        StateManager.ApplyLocal(new SetPassengerDestinations(coach.id, expectedSelectedDestinations.ToList()));
                    }
                }
            }

            logger.Information("Checking if train is in alarka or cochran and Passenger Mode to see if we need to reverse engine direction");
            bool atAlarka = currentStopIdentifier == alarkaIdentifier && !passengerLocomotive.TrainStatus.AtAlarka;
            bool atCochran = currentStopIdentifier == cochranIdentifier && !passengerLocomotive.TrainStatus.AtCochran && !orderedStopAtStations.Contains(alarkaIdentifier);

            if (!settings.LoopMode)
            {
                if (atAlarka)
                {
                    passengerLocomotive.TrainStatus.AtAlarka = true;
                    logger.Information("Train is in Alarka, there are more stops, and loop mode is not activated. Reversing train.");
                    Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Arrived in Alarka, reversing direction to continue.\"");
                    passengerLocomotive.ReverseLocoDirection();
                }
                else
                {
                    passengerLocomotive.TrainStatus.AtAlarka = false;
                }

                if (atCochran)
                {
                    passengerLocomotive.TrainStatus.AtCochran = true;
                    logger.Information("Train is in Cochran, there are more stops, loop mode is not activated and alarka is not a selected station. Reversing train.");
                    Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Arrived in Cochran, reversing direction to continue.\"");
                    passengerLocomotive.ReverseLocoDirection();
                }
                else
                {
                    passengerLocomotive.TrainStatus.AtCochran = false;
                }
            }
        }

        return false;
    }

    private bool RunTerminusStationProcedure(PassengerLocomotive passengerLocomotive, PassengerLocomotiveSettings settings, PassengerStop CurrentStop, IEnumerable<Car> coaches, List<string> orderedStopAtStations, List<string> orderedTerminusStations, DirectionOfTravel directionOfTravel)
    {
        // we have reached the last station
        if (settings.StopAtLastStation)
        {
            logger.Information("Pausing at last station due to setting");
            passengerLocomotive.PostNotice("ai-stop", $"Paused at last station stop {Hyperlink.To(CurrentStop)}.");
            passengerLocomotive.TrainStatus.CurrentlyStopped = true;
            passengerLocomotive.TrainStatus.CurrentReasonForStop = "Requested pause at last station";
        }

        logger.Information("{0} reached terminus station at {1}", passengerLocomotive._locomotive.DisplayName, CurrentStop.DisplayName);
        Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Reached terminus station at {Hyperlink.To(CurrentStop)}.\"");

        logger.Information("Re-selecting station stops based on settings.");

        // transfer station check
        int numTransferStations = settings.Stations.Where(s => s.Value.PassengerMode == PassengerMode.Transfer && s.Value.StopAt).Count();
        bool transferStationSelected = numTransferStations > 0;

        string currentStopIdentifier = CurrentStop.identifier;
        int westTerminusIndex = orderedStopAtStations.IndexOf(orderedTerminusStations[1]);
        int eastTerminusIndex = orderedStopAtStations.IndexOf(orderedTerminusStations[0]);
        int currentIndex = orderedStopAtStations.IndexOf(currentStopIdentifier);

        HashSet<string> expectedSelectedDestinations = orderedStopAtStations.ToHashSet();

        if (transferStationSelected)
        {
            logger.Information("Transfer station selected, checking direction and modifying expected selected stations");
            List<string> pickUpPassengerStations = settings.Stations.Where(s => s.Value.PickupPassengers).Select(s => s.Key).OrderBy(d => orderedStations.IndexOf(d)).ToList();
            int westTerminusIndex_Pickup = pickUpPassengerStations.IndexOf(orderedTerminusStations[1]);
            int eastTerminusIndex_Pickup = pickUpPassengerStations.IndexOf(orderedTerminusStations[0]);

            logger.Information("The following stations are pickup stations: {0}", pickUpPassengerStations);

            if (orderedTerminusStations[1] == "cochran" || orderedTerminusStations[1] == "alarka")
            {
                if (currentIndex == westTerminusIndex)
                {
                    logger.Information("train is currently at {0}, so select ALL pickup stations", currentStopIdentifier);
                    expectedSelectedDestinations.UnionWith(pickUpPassengerStations);
                }
            }
            else
            {

                if (directionOfTravel == DirectionOfTravel.WEST)
                {
                    logger.Information("Selecting pickup stations {0} that are further west of the west terminus station: {1}", pickUpPassengerStations.GetRange(westTerminusIndex_Pickup, pickUpPassengerStations.Count - westTerminusIndex_Pickup), orderedTerminusStations[1]);
                    // select all to the west of the west terminus station
                    expectedSelectedDestinations.UnionWith(pickUpPassengerStations.GetRange(westTerminusIndex_Pickup, pickUpPassengerStations.Count - westTerminusIndex_Pickup));
                }

                if (directionOfTravel == DirectionOfTravel.EAST)
                {
                    logger.Information("Selecting pickup stations {0} that are further east of the east terminus station: {1}", pickUpPassengerStations.GetRange(0, eastTerminusIndex_Pickup + 1), orderedTerminusStations[0]);
                    // select all to the east of the east terminus station
                    expectedSelectedDestinations.UnionWith(pickUpPassengerStations.GetRange(0, eastTerminusIndex_Pickup + 1));
                }
            }
        }

        logger.Information("Setting the following stations: {0}", expectedSelectedDestinations);

        foreach (Car coach in coaches)
        {
            foreach (string identifier in expectedSelectedDestinations)
            {
                logger.Information(string.Format("Applying {0} to car {1}", identifier, coach.DisplayName));
            }

            StateManager.ApplyLocal(new SetPassengerDestinations(coach.id, expectedSelectedDestinations.ToList()));
        }

        logger.Information("Checking to see if train is approaching terminus from outside of terminus bounds");
        if (settings.DirectionOfTravel == DirectionOfTravel.UNKNOWN)
        {
            logger.Information("Direction of travel is unknown");

            if (passengerLocomotive.PreviousStation == null)
            {
                logger.Information("train was not previously at a station.");
                logger.Information("Waiting for input from engineer about which direction to travel in");
                Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Unknown Direction. Pausing until I receive Direction of Travel via PassengerSettings.\"");
                passengerLocomotive.PostNotice("ai-stop", $"Paused, Unknown Direction at {Hyperlink.To(CurrentStop)}.");

                passengerLocomotive.TrainStatus.CurrentlyStopped = true;
                passengerLocomotive.TrainStatus.CurrentReasonForStop = "At Terminus Station and have an unknown direction.";
                return true;
            }
            else
            {
                logger.Information("train was  previously at a station.");
                string prevStopId = passengerLocomotive.PreviousStation.identifier;

                logger.Information("Checking if previous stop {0} was inside terminus bounds", prevStopId);
                if (orderedStopAtStations.Contains(prevStopId))
                {
                    logger.Information("Previous stop was inside terminus bounds, therefore proceed with normal loop/point to point logic");
                    settings.DirectionOfTravel = directionOfTravel;
                    TerminusStationReverseDirectionProcedure(passengerLocomotive, settings);
                }
                else
                {
                    logger.Information("We arrived at the terminus station from outside the bounds, therefore we should proceed in the current direction");
                }
            }
        }
        else
        {
            logger.Information("Direction of travel is known");

            if (settings.DirectionOfTravel == directionOfTravel)
            {
                logger.Information("The current direction of travel is the same as the new direction of travel.");
            }
            else
            {
                logger.Information("The new direction of travel is opposite current direction of travel");

                settings.DirectionOfTravel = directionOfTravel;
                settingsManager.SaveSettings();

                TerminusStationReverseDirectionProcedure(passengerLocomotive, settings);
            }
        }

        return false;
    }

    private void TerminusStationReverseDirectionProcedure(PassengerLocomotive passengerLocomotive, PassengerLocomotiveSettings settings)
    {
        logger.Information("Checking if in loop mode");
        // if we don't want to reverse, return to original logic
        if (settings.LoopMode)
        {
            logger.Information("Loop Mode is set to true. Continuing in current direction.");
            return;
        }

        logger.Information("Reversing direction");
        Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Reversing direction.\"");

        // reverse the direction of the loco
        passengerLocomotive.ReverseLocoDirection();
    }

    private void NotAtASelectedStationProcedure(PassengerLocomotive passengerLocomotive, PassengerLocomotiveSettings settings, PassengerStop CurrentStop, List<string> orderedSelectedStations, List<string> orderedTerminusStations)
    {
        logger.Information("Train is at a station: {0} that is not within the terminus bounds: {1}", CurrentStop.identifier, orderedTerminusStations);

        if (settings.DirectionOfTravel == DirectionOfTravel.UNKNOWN)
        {
            logger.Information("Travel direction is unknown, so unable to correct. Continuing in the current direction and will check again at the next station");
            return;
        }

        logger.Information("Travel direction is known.");
        logger.Information("Getting direction train should continue in");

        int currentStationIndex = orderedStations.IndexOf(CurrentStop.identifier);
        int indexWestTerminus_All = orderedStations.IndexOf(orderedTerminusStations[1]);
        int indexEastTerminus_All = orderedStations.IndexOf(orderedTerminusStations[0]);

        if (currentStationIndex > indexEastTerminus_All && currentStationIndex < indexWestTerminus_All)
        {
            logger.Information("station is inbounds of the terminus stations");
            return;
        }

        // if not in bounds of terminus
        if (currentStationIndex < indexEastTerminus_All)
        {
            if (settings.DirectionOfTravel == DirectionOfTravel.WEST)
            {
                logger.Information("train is already going in right direction to east terminus station {0} -> {1}", CurrentStop.identifier, orderedTerminusStations[0]);
            }
            else if (settings.DirectionOfTravel == DirectionOfTravel.EAST)
            {
                logger.Information("train is going wrong way from east terminus, revering direction based on loop/point to point setting");
                logger.Information("Checking if in loop mode");

                if (settings.LoopMode)
                {
                    logger.Information("Loop Mode is set to true. Continuing in current direction.");
                    Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Continuing direction to loop back to East Terminus.\"");
                }
                else
                {
                    logger.Information("Reversing direction");
                    Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Reversing direction to return back to East Terminus.\"");

                    // reverse the direction of the loco
                    passengerLocomotive.ReverseLocoDirection();
                }
            }
        }
        else if (currentStationIndex > indexWestTerminus_All)
        {
            if (settings.DirectionOfTravel == DirectionOfTravel.EAST)
            {
                logger.Information("train is already going in right direction to west terminus station {0} -> {1}", CurrentStop.identifier, orderedTerminusStations[1]);
            }
            else if (settings.DirectionOfTravel == DirectionOfTravel.WEST)
            {
                logger.Information("train is going wrong way from west terminus, revering direction based on loop/point to point setting");
                logger.Information("Checking if in loop mode");

                if (settings.LoopMode)
                {
                    logger.Information("Loop Mode is set to true. Continuing in current direction.");
                    Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Continuing direction to loop back to West Terminus.\"");
                }
                else
                {
                    logger.Information("Reversing direction");
                    Say($"AI Engineer {Hyperlink.To(passengerLocomotive._locomotive)}: \"Reversing direction to return back to West Terminus.\"");

                    // reverse the direction of the loco
                    passengerLocomotive.ReverseLocoDirection();
                }
            }
        }

    }

    private void Say(string message)
    {
        Multiplayer.Broadcast(message);
    }
}