using DiscordRPC;
using Humanizer;
using Humanizer.Localisation;
using Ryujinx.Common;
using Ryujinx.HLE.Loaders.Processes;
using Ryujinx.UI.App.Common;
using Ryujinx.UI.Common.Configuration;
using System.Linq;
using System.Text;

namespace Ryujinx.UI.Common
{
    public static class DiscordIntegrationModule
    {
        public static Timestamps StartedAt { get; set; }
        
        private const string Description = "A simple, experimental Nintendo Switch emulator.";
        private const string ApplicationId = "1341170671497117746";

        private const int ApplicationByteLimit = 128;
        private const string Ellipsis = "…";

        private static DiscordRpcClient _discordClient;
        private static RichPresence _discordPresenceMain;

        public static void Initialize()
        {
            _discordPresenceMain = new RichPresence
            {
                Assets = new Assets
                {
                    LargeImageKey = "ryujinx",
                    LargeImageText = TruncateToByteLength(Description)
                },
                Details = "Main Menu",
                State = "Idling",
                Timestamps = StartedAt
            };

            ConfigurationState.Instance.EnableDiscordIntegration.Event += Update;
        }

        private static void Update(object sender, ReactiveEventArgs<bool> evnt)
        {
            if (evnt.OldValue != evnt.NewValue)
            {
                // If the integration was active, disable it and unload everything
                if (evnt.OldValue)
                {
                    _discordClient?.Dispose();

                    _discordClient = null;
                }

                // If we need to activate it and the client isn't active, initialize it
                if (evnt.NewValue && _discordClient == null)
                {
                    _discordClient = new DiscordRpcClient(ApplicationId);

                    _discordClient.Initialize();
                    _discordClient.SetPresence(_discordPresenceMain);
                }
            }
        }

        public static void SwitchToPlayingState(ApplicationMetadata appMeta, ProcessResult procRes)
        {
            _discordClient?.SetPresence(new RichPresence
            {
                Assets = new Assets
                {
                    LargeImageKey = _discordGameAssetKeys.Contains(procRes.ProgramIdText) ? procRes.ProgramIdText : "game",
                    LargeImageText = TruncateToByteLength($"{appMeta.Title} (v{procRes.DisplayVersion})"),
                    SmallImageKey = "ryujinx",
                    SmallImageText = TruncateToByteLength(Description)
                },
                Details = TruncateToByteLength($"Playing {appMeta.Title}"),
                State = appMeta.LastPlayed.HasValue && appMeta.TimePlayed.TotalSeconds > 5
                    ? $"Total play time: {appMeta.TimePlayed.Humanize(2, false, maxUnit: TimeUnit.Hour)}"
                    : "Never played",
                Timestamps = Timestamps.Now
            });
        }

        public static void SwitchToMainState() => _discordClient?.SetPresence(_discordPresenceMain);

        private static string TruncateToByteLength(string input)
        {
            if (Encoding.UTF8.GetByteCount(input) <= ApplicationByteLimit)
            {
                return input;
            }

            // Find the length to trim the string to guarantee we have space for the trailing ellipsis.
            int trimLimit = ApplicationByteLimit - Encoding.UTF8.GetByteCount(Ellipsis);

            // Make sure the string is long enough to perform the basic trim.
            // Amount of bytes != Length of the string
            if (input.Length > trimLimit)
            {
                // Basic trim to best case scenario of 1 byte characters.
                input = input[..trimLimit];
            }

            while (Encoding.UTF8.GetByteCount(input) > trimLimit)
            {
                // Remove one character from the end of the string at a time.
                input = input[..^1];
            }

            return input.TrimEnd() + Ellipsis;
        }

        public static void Exit()
        {
            _discordClient?.Dispose();
        }

        private static readonly string[] _discordGameAssetKeys =
        [
                // All games are in Alphabetical order by Game name.
            
                          // Dragon Quest Franchise
            "010008900705c000", // Dragon Quest Builders
            "010042000a986000", // Dragon Quest Builders 2
            
                          // Fire Emblem Franchise
            "0100a6301214e000", // Fire Emblem Engage
            "0100a12011cc8000", // Fire Emblem: Shadow Dragon
            "010055d009f78000", // Fire Emblem: Three Houses
            "0100f15003e64000", // Fire Emblem Warriors
            "010071f0143ea000", // Fire Emblem Warriors: Three Hopes
            
                          // Kirby Franchise
            "01004d300c5ae000", // Kirby and the Forgotten Land
            "0100a8e016236000", // Kirby's Dream Buffet
            "0100227010460000", // Kirby Fighters 2
            "01006b601380e000", // Kirby's Return to Dream Land Deluxe
            "01007e3006dda000", // Kirby Star Allies
            "01003fb00c5a8000", // Super Kirby Clash
            
                          // Zelda Franchise
            "01000b900d8b0000", // Cadence of Hyrule
            "0100ae00096ea000", // Hyrule Warriors: Definitive Edition
            "01002b00111a2000", // Hyrule Warriors: Age of Calamity
            "01007ef00011e000", // The Legend of Zelda: Breath of the Wild
            "01006bb00c6f0000", // The Legend of Zelda: Link's Awakening
            "01002da013484000", // The Legend of Zelda: Skyward Sword HD
            "0100f2c0115b6000", // The Legend of Zelda: Tears of the Kingdom
            "01008cf01baac000", // The Legend of Zelda: Echoes of Wisdom
            
                          // Luigi Franchise
            "010048701995e000", // Luigi's Mansion 2 HD
            "0100dca0064a6000", // Luigi's Mansion 3
            
                          // Metroid Franchise
            "010093801237c000", // Metroid Dread
            "010012101468c000", // Metroid Prime Remastered
            
                          // Monster Hunter Franchise
            "0100770008dd8000", // Monster Hunter Generations Ultimate
            "0100b04011742000", // Monster Hunter Rise 
            
                          // Mario Franchise
            "010021d00812a000", // Arcade Archives VS. SUPER MARIO BROS.
            "01006d0017f7a000", // Mario & Luigi: Brothership
            "010003000e146000", // Mario & Sonic at the Olympic Games Tokyo 2020
            "010067300059a000", // Mario + Rabbids: Kingdom Battle
            "0100317013770000", // Mario + Rabbids: Sparks of Hope
            "0100c9c00e25c000", // Mario Golf: Super Rush
            "0100152000022000", // Mario Kart 8 Deluxe
            "01006fe013472000", // Mario Party Superstars
            "010019401051c000", // Mario Strikers: Battle League
            "0100bde00862a000", // Mario Tennis Aces
            "0100b99019412000", // Mario vs. Donkey Kong
            "010049900f546000", // Super Mario 3D All-Stars
            "010028600ebda000", // Super Mario 3D World + Bowser's Fury
            "010049900F546001", // Super Mario 64
            "0100ea80032ea000", // Super Mario Bros. U Deluxe
            "010015100b514000", // Super Mario Bros. Wonder
            "010049900F546003", // Super Mario Galaxy
            "01009b90006dc000", // Super Mario Maker 2
            "0100000000010000", // SUPER MARIO ODYSSEY
            "010036b0034e4000", // Super Mario Party
            "0100965017338000", // Super Mario Party Jamboree
            "0100bc0018138000", // Super Mario RPG
            "010049900F546002", // Super Mario Sunshine
            "0100a3900c3e2000", // Paper Mario: The Origami King
            "0100ecd018ebe000", // Paper Mario: The Thousand-Year Door
            
                          // Pikmin Franchise
            "0100aa80194b0000", // Pikmin 1
            "0100d680194b2000", // Pikmin 2
            "0100f4c009322000", // Pikmin 3 Deluxe
            "0100b7c00933a000", // Pikmin 4
            
                          // Pokémon Franchise
            "0100f4300bf2c000", // New Pokémon Snap
            "0100000011d90000", // Pokémon Brilliant Diamond
            "01001f5010dfa000", // Pokémon Legends: Arceus
            "01003d200baa2000", // Pokémon Mystery Dungeon: Rescue Team DX
            "0100a3d008c5c000", // Pokémon Scarlet
            "01008db008c2c000", // Pokémon Shield
            "010018e011d92000", // Pokémon Shining Pearl
            "0100abf008968000", // Pokémon Sword
            "01008f6008c5e000", // Pokémon Violet
            "0100b3f000be2000", // Pokkén Tournament DX
            "0100187003a36000", // Pokémon: Let's Go Eevee!
            "010003f003a34000", // Pokémon: Let's Go Pikachu!
            
                          // Splatoon Franchise
            "0100f8f0000a2000", // Splatoon 2 (EU)
            "01003c700009c000", // Splatoon 2 (JP)
            "01003bc0000a0000", // Splatoon 2 (US)
            "0100c2500fc20000", // Splatoon 3
            "0100ba0018500000", // Splatoon 3: Splatfest World Premiere
            
                          // NSO Membership games
            "0100c62011050000", // GB - Nintendo Switch Online
            "010012f017576000", // GBA - Nintendo Switch Online
            "0100c9a00ece6000", // N64 - Nintendo Switch Online
            "0100e0601c632000", // N64 - Nintendo Switch Online 18+
            "0100d870045b6000", // NES - Nintendo Switch Online
            "0100b3c014bda000", // SEGA Genesis - Nintendo Switch Online
            "01008d300c50c000", // SNES - Nintendo Switch Online
            "0100ccf019c8c000", // F-ZERO 99
            "0100ad9012510000", // PAC-MAN 99
            "010040600c5ce000", // Tetris 99
            "0100277011f1a000", // Super Mario Bros. 35
            
                          // Misc Nintendo 1st party games
            "01000320000cc000", // 1-2 Switch
            "0100300012f2a000", // Advance Wars 1+2: Re-Boot Camp
            "01006f8002326000", // Animal Crossing: New Horizons
            "0100620012d6e000", // Big Brain Academy: Brain vs. Brain
            "010018300d006000", // BOXBOY! + BOXGIRL!
            "01009d901bc56000", // Donkey Kong Country Returns HD
            "0100c1f0051b6000", // Donkey Kong Country: Tropical Freeze
            "0100ed000d390000", // Dr. Kawashima's Brain Training
            "010067b017588000", // Endless Ocean Luminous
            "0100d2f00d5c0000", // Nintendo Switch Sports
            "01006b5012b32000", // Part Time UFO
            "01007a3009184000", // Princess Peach: Showtime
            "0100704000B3A000", // Snipperclips
            "01006a800016e000", // Super Smash Bros. Ultimate
            "0100a9400c9c2000", // Tokyo Mirage Sessions #FE Encore
            
                          // Bayonetta Franchise
            "010076f0049a2000", // Bayonetta
            "01007960049a0000", // Bayonetta 2
            "01004a4010fea000", // Bayonetta 3
            "0100cf5010fec000", // Bayonetta Origins: Cereza and the Lost Demon
            
                          // Persona Franchise
            "0100dcd01525a000", // Persona 3 Portable
            "010075a016a3a000", // Persona 4 Arena Ultimax
            "010062b01525c000", // Persona 4 Golden
            "01005ca01580e000", // Persona 5 Royal
            "0100801011c3e000", // Persona 5 Strikers
            "010087701b092000", // Persona 5 Tactica
            
                          // Sonic Franchise
            "01004ad014bf0000", // Sonic Frontiers
            "01009aa000faa000", // Sonic Mania
            "01005ea01c0fc000", // SONIC X SHADOW GENERATIONS
            "01005ea01c0fc001", // ^
            
                          // Xenoblade Franchise
            "0100ff500e34a000", // Xenoblade Chronicles: Definitive Edition
            "0100e95004038000", // Xenoblade Chronicles 2
            "010074f013262000", // Xenoblade Chronicles 3
            "0100453019aa8000", // Xenoblade Chronicles X: Definitive Edition
            
                          // Baten Kaitos Franchise
            "0100c07018ca6000", // Baten Kaitos I & II HD Remaster
            "0100c07018ca6001", // Baten Kaitos
            "0100c07018ca6002", // Baten Kaitos Origins
            
                          // Crash Bandicoot Franchise
            "0100d1b006744000", // Crash Bandicoot N. Sane Trilogy
            "0100f9f00c696000", // Crash Team Racing Nitro Fueled
            
                         // Ori Franchise
            "010061d00db74000", // Ori and the Blind Forest: Definitive Edition
            "01008dd013200000", // Ori and the Will of the Wisps
            
                         // Star Ocean Franchise
            "0100ebf00e702000", // Star Ocean First Departure R
            "010065301a2e0000", // Star Ocean The Second Story R
            
                          // Misc Games
            "010056e00853a000", // A Hat in Time
            "010020d01ad24000", // Animal Well
            "0100a21017c42000", // Another Crab's Treasure
            "01007300020fa000", // Astral Chain
            "0100fd1014726000", // Baldur's Gate: Dark Alliance
            "01008c2019598000", // Bluey: The Video Game
            "0100e0601dc2a000", // Bo: Path of the Teal Lotus
            "010089c00b026000", // Braid: Anniversary Edition
            "0100c6800b934000", // Brawlhalla
            "0100dbf01000a000", // Burnout Paradise Remastered
            "0100744001588000", // Cars 3: Driven to Win
            "0100fa501af90000", // Castlevania: Dominus Collection
            "01002b30028f6000", // Celeste
            "0100c510166f0000", // Chained Echoes
            "0100b41013c82000", // Cruis'n Blast
            "0100cf401a98e000", // Contra: Operation Galuga
            "010085900337e000", // Death Squared
            "0100309016e7a000", // Demon Slayer -Kimestsu no Yaiba- The Hinokami Chronicles
            "01001b300b9be000", // Diablo III: Eternal Collection
            "010027400cdc6000", // Divinity: Original Sin 2 - Definitive Edition
            "010032c00ac58000", // Dragon's Dogma: Dark Arisen
            "01008c8012920000", // Dying Light Platinum Edition
            "0100ed9018f3e000", // Eiyuden Chronicle: Hundred Heroes
            "0100da201ebf8000", // Epic Mickey: Rebrushed
            "0100eb100ab42000", // Final Fantasy XII: The Zodiac Age
            "01001cc01b2d4000", // Goat Simulator 3
            "0100f300169b6000", // Grime
            "0100535012974000", // Hades
            "01003620068ea000", // Hand of Fate 2
            "0100f7e00c70e000", // Hogwarts Legacy
            "010079901c898000", // Legacy of Kain: Soul Reaver 1 & 2 Remastered
            "010085500130a000", // Lego City: Undercover
            "010073c01af34000", // LEGO Horizon Adventures
            "0100dcd01e9ee000", // LUNAR Remastered Collection
            "010069301e220000", // Marvel vs. Capcom Fighting Collection: Arcade Classics
            "0100d71004694000", // Minecraft
            "01007430037f6000", // Monopoly
            "0100853015e86000", // No Man's Sky
            "0100276009872000", // Okami HD
            "01007bb017812000", // Portal
            "0100abd01785c000", // Portal 2
            "01008e200c5c2000", // Muse Dash
            "01001d301b83a000", // Neva
            "0100be7013e06000", // Ni No Kuni II: Revenant Kingdom
            "0100d810170d0000", // Nine Sols
            "010057d006492000", // Octopath Traveler
            "0100210019428000", // Prince of Persia: The Lost Crown
            "01007820196a6000", // Red Dead Redemption
            "0100e8300a67a000", // Risk
            "01002f7013224000", // Rune Factory 5
            "01008d100d43e000", // Saints Row IV
            "0100de600beee000", // Saints Row: The Third - The Full Package
            "01006bf01c4c8000", // Shiren the Wanderer: The Mystery Dungeon of Serpentcoil Island
            "01001180021fa000", // Shovel Knight: Specter of Torment
            "0100e65002bb8000", // Stardew Valley
            "0100d7a01b7a2000", // Star Wars: Bounty Hunter
            "0100800015926000", // Suika Game
            "01004bd017080000", // Suikoden I & II HD Remaster
            "01007ad00013e000", // Super Bomberman R
            "0100e46006708000", // Terraria
            "0100605008268000", // Titan Quest
            "01000a10041ea000", // The Elder Scrolls V: Skyrim
            "0100c1500b82e000", // The World Ends with You: Final Remix
            "010024601bb16000", // Tomb Raider I-III Remastered
            "010057a01e4d4000", // TSUKIHIME -A piece of blue glass moon-
            "010080b00ad66000", // Undertale
            "010069401adb8000", // Unicorn Overlord
            "010018401eea8000", // WitchSpring R
            "01005c600ac68000", // Valkyria Chronicles 4
            "0100534009ff2000", // Yonder: The Cloud Catcher Chronicles
        ];
    }
}
