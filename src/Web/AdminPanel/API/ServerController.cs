using BCrypt.Net;

namespace MUnique.OpenMU.Web.API
{
    using Microsoft.AspNetCore.Mvc;
    using MUnique.OpenMU.AttributeSystem;
    using MUnique.OpenMU.DataModel;
    using MUnique.OpenMU.DataModel.Configuration.Items;
    using MUnique.OpenMU.DataModel.Entities;
    using MUnique.OpenMU.GameLogic;
    using MUnique.OpenMU.GameLogic.Attributes;
    using MUnique.OpenMU.GameServer;
    using MUnique.OpenMU.Interfaces;
    using MUnique.OpenMU.Persistence;
    using System.Text.Json;

    /// <summary>
    /// Server API controller
    /// </summary>
    [Route("api/")]
    public class ServerController : Controller
    {
        private IDictionary<int, IGameServer> _gameServers;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="gameServers"></param>
        public ServerController(IDictionary<int, IGameServer> gameServers) => _gameServers = gameServers;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="id"></param>
        /// <param name="msg"></param>
        /// <returns></returns>
        [Route("send/{id=0}")]
        public async Task<IActionResult> SendGlobalMessage(int id, [FromQuery(Name = "msg")] string msg)
        {
            var server = (GameServer)_gameServers.Values.ElementAt(id);
            if (server is not null)
            {
                await server.Context.SendGlobalNotificationAsync(msg).ConfigureAwait(false);
                return Ok("Done");
            }
            return Ok("Server not ready");
        }

        /// <summary>
        /// Creates a new game account with a starting character (for testing/demo).
        /// POST /api/account/create
        /// Body: { "loginName": "fashi01", "password": "fashi01", "characterName": "fashi01", "characterClass": 0 }
        /// </summary>
        [HttpPost]
        [Route("account/create")]
        public async Task<IActionResult> CreateAccount([FromBody] CreateAccountRequest request)
        {
            var server = this._gameServers.Values.OfType<GameServer>().FirstOrDefault();
            if (server is null) return this.BadRequest(new { error = "No game server available" });

            var context = server.Context;
            var config = context.Configuration;

            using var persistContext = context.PersistenceContextProvider.CreateNewPlayerContext(config);
            var account = persistContext.CreateNew<Account>();
            if (account is null) return this.BadRequest(new { error = "Failed to create account" });

            account.LoginName = request.LoginName;
            account.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
            account.State = AccountState.Normal;
            account.Vault = persistContext.CreateNew<ItemStorage>();

            var charClass = config.CharacterClasses.FirstOrDefault(c => c.Number == request.CharacterClass)
                           ?? config.CharacterClasses.FirstOrDefault();
            if (charClass is null) return this.BadRequest(new { error = "No character class found" });

            var character = persistContext.CreateNew<Character>();
            if (character is null) return this.BadRequest(new { error = "Failed to create character" });

            character.Name = request.CharacterName;
            character.CharacterClass = charClass;
            character.Inventory = persistContext.CreateNew<ItemStorage>();

            // Add potions to main inventory (slots 12-17, after equipment slots 0-11)
            var potionDefs = config.Items.Where(i => i.Group == 14 && i.Number is >= 1 and <= 6).ToDictionary(i => i.Number);
            byte slot = InventoryConstants.EquippableSlotsCount; // 12 = first inventory slot after equipment
            foreach (var num in new byte[] { 1, 2, 3, 4, 5, 6 })
            {
                if (potionDefs.TryGetValue(num, out var def))
                {
                    var potionItem = persistContext.CreateNew<Item>();
                    potionItem.Definition = def;
                    potionItem.ItemSlot = slot++;
                    potionItem.Durability = 3;
                    character.Inventory.Items.Add(potionItem);
                }
            }

            // Add only starting skills (not all class skills), so skill books remain learnable
            var startingSkillNumbers = new Dictionary<int, int[]>
            {
                { 0, new[] { 17 } },    // DarkWizard: EnergyBall
                { 4, new[] { 44 } },    // DarkKnight: CrescentMoonSlash
                { 8, new[] { 46 } },    // FairyElf: Starfall
                { 12, new[] { 73, 57 } }, // MagicGladiator: ManaRays, SpiralSlash
                { 16, new[] { 60, 74 } }, // DarkLord: Force, FireBlast
                { 20, new[] { 45 } },   // Summoner: Lance
                { 24, new[] { 27 } },   // RageFighter: Charge
            };
            if (startingSkillNumbers.TryGetValue(charClass.Number, out var skillNums))
            {
                foreach (var skillNum in skillNums)
                {
                    var skill = config.Skills.FirstOrDefault(s => s.Number == skillNum);
                    if (skill is not null)
                    {
                        var skillEntry = persistContext.CreateNew<SkillEntry>();
                        skillEntry.Skill = skill;
                        skillEntry.Level = 0;
                        character.LearnedSkills.Add(skillEntry);
                    }
                }
            }

            // Set base stats from class definition
            character.Attributes.Clear();
            if (charClass.StatAttributes is not null)
            {
                foreach (var statDef in charClass.StatAttributes)
                {
                    var statType = statDef.Attribute;
                    if (statType is null) continue;
                    var attr = persistContext.CreateNew<StatAttribute>(statType, statDef.BaseValue);
                    character.Attributes.Add(attr);
                }
            }

            // Fallback: if no stats were loaded, add minimum defaults
            if (character.Attributes.Count == 0)
            {
                character.Attributes.Add(persistContext.CreateNew<StatAttribute>(
                    config.Attributes.First(a => a.Designation == "Strength"), 18));
                character.Attributes.Add(persistContext.CreateNew<StatAttribute>(
                    config.Attributes.First(a => a.Designation == "Agility"), 18));
                character.Attributes.Add(persistContext.CreateNew<StatAttribute>(
                    config.Attributes.First(a => a.Designation == "Energy"), 15));
                character.Attributes.Add(persistContext.CreateNew<StatAttribute>(
                    config.Attributes.First(a => a.Designation == "Vitality"), 15));
            }

            character.Experience = 0;
            var pointsPerLevel = charClass.StatAttributes is not null
                ? charClass.StatAttributes.FirstOrDefault(a => a.Attribute == Stats.PointsPerLevelUp)?.BaseValue ?? 5
                : 5;
            character.LevelUpPoints = (int)pointsPerLevel;  // Give initial stat points

            // Set spawn position using class HomeMap's spawn gate
            character.CurrentMap = charClass.HomeMap ?? config.Maps.FirstOrDefault(m => m.Number == 0);
            byte spawnX = 130, spawnY = 130;
            if (character.CurrentMap is not null)
            {
                var spawnGate = character.CurrentMap.ExitGates.Where(g => g.IsSpawnGate).SelectRandom();
                if (spawnGate is not null)
                {
                    spawnX = (byte)Random.Shared.Next(spawnGate.X1, spawnGate.X2 + 1);
                    spawnY = (byte)Random.Shared.Next(spawnGate.Y1, spawnGate.Y2 + 1);
                }

                character.PositionX = spawnX;
                character.PositionY = spawnY;
            }

            account.Characters.Add(character);
            var saveOk = await persistContext.SaveChangesAsync().ConfigureAwait(false);
            if (!saveOk) return this.BadRequest(new { error = "Failed to save account to database" });

            return this.Ok(new
            {
                loginName = request.LoginName,
                characterName = request.CharacterName,
                className = charClass.Name,
                mapNumber = character.CurrentMap?.Number ?? 0,
                x = spawnX,
                y = spawnY,
                message = "Account created. Use Main.exe to connect.",
            });
        }

        /// <summary>API response</summary>
        [Serializable]
        public sealed class CreateAccountRequest
        {
            /// <summary>Account login name.</summary>
            public string LoginName { get; set; } = string.Empty;
            /// <summary>Account password.</summary>
            public string Password { get; set; } = string.Empty;
            /// <summary>Character name.</summary>
            public string CharacterName { get; set; } = string.Empty;
            /// <summary>Character class number (0=DarkWizard, 4=DarkKnight, etc).</summary>
            public int CharacterClass { get; set; }
        }

        /// <summary>
        /// Gets a flag, if the specified account is currently online.
        /// </summary>
        /// <param name="accountName">Name of the account.</param>
        /// <returns>True, when online.</returns>
        [HttpGet]
        [Route("is-online/{accountName=0}")]
        public async Task<bool> GetIsOnlineAsync(string accountName)
        {
            var isOnline = false;

            foreach (var server in this._gameServers.Values.OfType<GameServer>())
            {
                var players = await server.Context.GetPlayersAsync().ConfigureAwait(false);
                if (players.Any(p => p.Account?.LoginName == accountName))
                {
                    isOnline = true;
                    break;
                }
            }

            return isOnline;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("status")]
        public IActionResult ServerState()
        {
            int sum = 0;
            var list = new List<string>();
            _gameServers.Values.ForEach(async item =>
            {
                var server = item as GameServer;
                if(server is not null)
                {
                    await server.Context.ForEachPlayerAsync(player =>
                    {
                        list.Add(player.GetName());
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                    sum = sum + server.Context.PlayerCount;
                }
            });

            var item = new
            {
                state = "Online",
                players = sum,
                playersList = list
            };

            return Ok(JsonSerializer.Serialize(item));
        }
    }
}