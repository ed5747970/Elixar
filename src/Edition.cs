// Elixar comes in two editions built from the same code:
//   Elixar for Private  - home PCs (free for personal and noncommercial use)
//   Elixar for Business - adds Policy manager, Sysprep and golden image tools, fleet reports, the Group Policy template,
//                         the Windows Event Log and the Cure Agent / Cure Console (business license)
// Both always obey an organization's policy (HKLM\SOFTWARE\Policies\Elixar) and lock Phone, Sysprep and Email on company PCs.
// The Business build is compiled with /define:BUSINESS.

namespace CDriveCleaner
{
    public static class Edition
    {
#if BUSINESS
        public static readonly bool Business = true;
        public const string Short = "Business";
#else
        public static readonly bool Business = false;
        public const string Short = "Private";
#endif
        public static string Name { get { return "Elixar for " + Short; } }
        // the release files of this edition (an update never swaps the edition)
        public static string ZipAsset { get { return Business ? "Elixar-Business.zip" : "Elixar.zip"; } }
        public static string ManifestAsset { get { return Business ? "elixar-business-manifest" : "elixar-manifest"; } }
        public static string Id { get { return Business ? "business" : "private"; } }
        public const string BusinessOnly = "This is part of Elixar for Business.";
    }
}
