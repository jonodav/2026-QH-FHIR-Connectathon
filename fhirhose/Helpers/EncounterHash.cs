using System.Text;

namespace Helpers
{
    public static class Hashing
    {
        public static string EncounterHash(string encounter, List<string> orderCodes)
        {
            var list = $"{encounter}-{string.Join("|", orderCodes)}";
            return list;
        }
    }
}