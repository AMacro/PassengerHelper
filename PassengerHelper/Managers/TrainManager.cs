namespace PassengerHelperPlugin.Managers;

using System.Collections.Generic;
using Support;
using Serilog;
using Model;
using System.Linq;

public class TrainManager
{
    static readonly Serilog.ILogger logger = Log.ForContext(typeof(TrainManager));
    // private Dictionary<string, BaseLocomotive> _locomotiveNameToLocomotive;
    private Dictionary<string, PassengerLocomotive> passengerLocomotives = new();

    private SettingsManager settingsManager;

    public TrainManager(SettingsManager settingsManager)
    {
        this.settingsManager = settingsManager;
    }

    public bool GetPassengerLocomotive(BaseLocomotive locomotive, out PassengerLocomotive passengerLocomotive)
    {
        if (!this.passengerLocomotives.TryGetValue(locomotive.DisplayName, out passengerLocomotive))
        {
            return false;
        }

        return true;
    }

    public PassengerLocomotive GetPassengerLocomotive(string locomotiveDisplayName)
    {
        return passengerLocomotives[locomotiveDisplayName];
    }

    public PassengerLocomotive GetPassengerLocomotive(BaseLocomotive locomotive)
    {
        logger.Information("Getting PassengerLocomotive for {0}", locomotive.DisplayName);
        if (!this.passengerLocomotives.TryGetValue(locomotive.DisplayName, out PassengerLocomotive passengerLocomotive))
        {
            logger.Information("Did not find existing PassengerLocomotive for {0}, looking for existing PassengerSettings and creating a new Passenger Locomotive", locomotive.DisplayName);
            passengerLocomotive = new PassengerLocomotive(locomotive, settingsManager.GetSettings(locomotive.DisplayName));

            logger.Information("Adding new Passenger Locomotive to internal Dictionary");

            this.passengerLocomotives.Add(locomotive.DisplayName, passengerLocomotive);
        }

        return passengerLocomotive;
    }
}