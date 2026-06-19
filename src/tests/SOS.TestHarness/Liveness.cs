namespace SOS.TestHarness;

[Flags]
public enum Liveness
{
    Live = 1,
    Dump = 2,
    AllValid = Live | Dump,
}
