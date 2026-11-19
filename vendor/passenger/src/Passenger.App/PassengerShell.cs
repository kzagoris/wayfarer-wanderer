using Passenger.Core;

namespace Passenger.App;

/// <summary>Passenger's whole shell run by another product's program: it names itself first (<see cref="Product.Identify"/>),
/// so its windows, state folder and helpers are its own, then hands its command line over as Passenger's Main reads one,
/// git's askpass and gpg calls included, since the helpers it names are that program.</summary>
public static class PassengerShell
{
    public static int Run(string name, string programName, string[] args)
    {
        Product.Identify(name, programName);
        return Program.Main(args);
    }
}
