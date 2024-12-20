using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Cache;
using ExileCore2.Shared.Nodes;
using ExileCore2.Shared;
using System.Numerics;
using System.Drawing;
using RectangleF = ExileCore2.Shared.RectangleF;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

// ReSharper disable CollectionNeverUpdated.Local

namespace ProximityAlert
{
    public partial class Proximity : BaseSettingsPlugin<ProximitySettings>
    {
        private static SoundController _soundController;
        private static Dictionary<string, Warning> _pathDict = new Dictionary<string, Warning>();
        private static Dictionary<string, Warning> _modDict = new Dictionary<string, Warning>();
        private static string _soundDir;
        private static bool _playSounds = true;
        private static DateTime _lastPlayed;
        private static readonly object Locker = new object();
        private static readonly List<StaticEntity> NearbyPaths = new List<StaticEntity>();
        private readonly Queue<Entity> _entityAddedQueue = new Queue<Entity>();
        private IngameState _ingameState;
        private RectangleF _windowArea;

        public override bool Initialise()
        {
            base.Initialise();
            Name = "Proximity Alerts";
            _ingameState = GameController.Game.IngameState;
            lock (Locker) _soundController = GameController.SoundController;
            _windowArea = GameController.Window.GetWindowRectangle();
            
            Graphics.InitImage(Path.Combine(DirectoryFullName, "textures\\Direction-Arrow.png").Replace('\\', '/'),
                false);
            Graphics.InitImage(Path.Combine(DirectoryFullName, "textures\\back.png").Replace('\\', '/'), false);
            lock (Locker) _soundDir = Path.Combine(DirectoryFullName, "sounds\\").Replace('\\', '/');
            _pathDict = LoadConfig(Path.Combine(DirectoryFullName, "PathAlerts.txt"));
            _modDict = LoadConfig(Path.Combine(DirectoryFullName, "ModAlerts.txt"));
            SetFonts();
            return true;
        }

        private static RectangleF Get64DirectionsUV(double phi, double distance, int rows)
        {
            phi += Math.PI * 0.25; // fix rotation due to projection
            if (phi > 2 * Math.PI) phi -= 2 * Math.PI;

            var xSprite = (float) Math.Round(phi / Math.PI * 32);
            if (xSprite >= 64) xSprite = 0;

            float ySprite = distance > 60 ? distance > 120 ? 2 : 1 : 0;
            var x = xSprite / 64;
            float y = 0;
            if (rows > 0)
            {
                y = ySprite / rows;
                return new RectangleF(x, y, (xSprite + 1) / 64 - x, (ySprite + 1) / rows - y);
            }

            return new RectangleF(x, y, (xSprite + 1) / 64 - x, 1);
        }

        private IEnumerable<string[]> GenDictionary(string path)
        {
            return File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)
                                                         && line.IndexOf(';') >= 0
                                                         && !line.StartsWith("#")).Select(line =>
                line.Split(new[] {';'}, 5).Select(parts => parts.Trim()).ToArray());
        }

        private static Color HexToColor(string value)
        {
            if (value.StartsWith("#")) value = value.Substring(1);
            if (value.Length == 6) value = "ff" + value; // Add full opacity if not specified
            return Color.FromArgb(
                int.Parse(value.Substring(0, 2), NumberStyles.HexNumber), // alpha
                int.Parse(value.Substring(2, 2), NumberStyles.HexNumber), // red
                int.Parse(value.Substring(4, 2), NumberStyles.HexNumber), // green
                int.Parse(value.Substring(6, 2), NumberStyles.HexNumber)  // blue
            );
        }

        private Dictionary<string, Warning> LoadConfig(string path)
        {
            //if (!File.Exists(path)) CreateConfig(path);
            return GenDictionary(path).ToDictionary(line => line[0], line =>
            {
                var distance = -1;
                if (int.TryParse(line[3], out var tmp)) distance = tmp;
                var preloadAlertConfigLine = new Warning
                    {Text = line[1], Color = HexToColor(line[2]), Distance = distance, SoundFile = line[4]};
                return preloadAlertConfigLine;
            });
        }

        public override void EntityAdded(Entity entity)
        {
            if (!Settings.Enable.Value) return;
            if (entity.Type == EntityType.Monster) _entityAddedQueue.Enqueue(entity);
        }

        public override void AreaChange(AreaInstance area)
        {
            try
            {
                _entityAddedQueue.Clear();
                NearbyPaths.Clear();
            }
            catch
            {
                // ignored
            }
        }


        public override void Tick()
        {
            TickLogic();
        }

        private void TickLogic()
        {
            while (_entityAddedQueue.Count > 0)
            {
                var entity = _entityAddedQueue.Dequeue();
                if (entity.IsValid && !entity.IsAlive) continue;
                if (!entity.IsHostile || !entity.IsValid) continue;
                if (!Settings.ShowModAlerts) continue;
                try
                {
                    if (entity.HasComponent<ObjectMagicProperties>() && entity.IsAlive)
                    {
                        var mods = entity.GetComponent<ObjectMagicProperties>().Mods;
                        if (mods != null)
                        {
                            var matchingMods = mods.Where(x => _modDict.ContainsKey(x)).ToList();
                            if (matchingMods.Any())
                            {
                                var filters = matchingMods.Select(mod => _modDict[mod]).ToList();
                                entity.SetHudComponent(new ProximityAlert(entity, filters));
                                lock (Locker)
                                {
                                    PlaySound(filters[0].SoundFile);
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // ignored
                }
            }

            // Update valid
            foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
            {
                var drawCmd = entity.GetHudComponent<ProximityAlert>();
                drawCmd?.Update();
            }
        }

        private static void PlaySound(string path)
        {
            if (!_playSounds) return;
            // Sanity Check because I'm too lazy to make a queue
            if ((DateTime.Now - _lastPlayed).TotalMilliseconds > 250)
            {
                if (path != string.Empty) {
                    _soundController.PreloadSound(Path.Combine(_soundDir, path).Replace('\\', '/'));
                    _soundController.PlaySound(Path.Combine(_soundDir, path).Replace('\\', '/'));
                }
                _lastPlayed = DateTime.Now;
            }
        }

        public override void Render()
        {
            try
            {
                _playSounds = Settings.PlaySounds;
                var height = (float) int.Parse(Settings.Font.Value.Substring(Settings.Font.Value.Length - 2));
                height = height * Settings.Scale;
                var margin = height / Settings.Scale / 4;

                if (!Settings.Enable) return;
                if (Settings.ShowPathAlerts)
                    foreach (var sEnt in NearbyPaths)
                    {
                        var entityScreenPos = _ingameState.Camera.WorldToScreen(sEnt.Pos.Translate(0, 0, 0));
                        var textWidth = Graphics.MeasureText(sEnt.Path, 10) * 0.73f;
                        var position = new Vector2(entityScreenPos.X - textWidth.X / 2, entityScreenPos.Y - 7);
                        Graphics.DrawBox(position, position+new Vector2(textWidth.X, 13), Color.FromArgb(200, 0, 0, 0));
                        Graphics.DrawText(
                            sEnt.Path,
                            new Vector2(entityScreenPos.X, entityScreenPos.Y),
                            Color.White,
                            FontAlign.Center
                        );
                    }

                if (Settings.ShowSirusLine)
                    foreach (var sEnt in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
                        .Where(x => x.Metadata.Equals("Metadata/Monsters/AtlasExiles/AtlasExile5")))
                    {
                        if (sEnt.Path.Contains("Throne") || sEnt.Path.Contains("Apparation")) break;
                        if (sEnt.DistancePlayer > 200) break;
                        var entityScreenPos = _ingameState.Camera.WorldToScreen(sEnt.Pos.Translate(0, 0, 0));
                        var playerPosition =
                            GameController.Game.IngameState.Camera.WorldToScreen(GameController.Player.Pos);
                        Graphics.DrawLine(playerPosition, entityScreenPos, 4, Color.FromArgb(140, 255, 0, 255));

                        Graphics.DrawText(sEnt.DistancePlayer.ToString(CultureInfo.InvariantCulture), new Vector2(0, 0));
                    }

                var unopened = "";
                var mods = "";
                var lines = 0;

                var origin = _windowArea.Center.Translate(Settings.ProximityX - 96, Settings.ProximityY);

                // mod Alerts
                foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
                {
                    // skip white mobs
                    if (entity.Rarity == MonsterRarity.White) continue;
                    var structValue = entity.GetHudComponent<ProximityAlert>();
                    if (structValue == null || !entity.IsAlive || structValue.Names == string.Empty) continue;
                    var delta = entity.GridPos - GameController.Player.GridPos;
                    var distance = delta.GetPolarCoordinates(out var phi);

                    var rectDirection = new RectangleF(origin.X - margin - height / 2,
                        origin.Y - margin / 2 - height - lines * height, height, height);
                    var rectUV = Get64DirectionsUV(phi, distance, 3);

                    if (!mods.Contains(structValue.Names))
                    {
                        var modLines = structValue.Names.Split('\n');
                        var lineCount = modLines.Length;
                        
                        // Calculate total height needed for all lines
                        var totalHeight = lineCount * height;
                        
                        // Draw each line of text with its own background
                        for (var i = 0; i < lineCount; i++)
                        {
                            var currentLine = modLines[i];
                            var position = new Vector2(origin.X + height / 2, origin.Y - (lines + i + 1) * height);
                            var textSize = Graphics.MeasureText(currentLine);
                            
                            // Draw background box for this line
                            Graphics.DrawBox(
                                position, 
                                position + new Vector2(textSize.X, height), 
                                Color.FromArgb(200, 0, 0, 0)
                            );
                            
                            // Draw the text
                            Graphics.DrawText(
                                currentLine,
                                position,
                                structValue.Color
                            );
                        }

                        // Draw direction arrow for the entire mod group
                        Graphics.DrawImage("Direction-Arrow.png", rectDirection, rectUV, structValue.Color);
                        
                        mods += structValue.Names;
                        lines += lineCount;
                    }
                }

                // entities
                foreach (var entity in GameController.EntityListWrapper.Entities
                    .Where(x => x.Type == EntityType.Chest ||
                                x.Type == EntityType.Monster ||
                                x.Type == EntityType.IngameIcon ||
                                x.Type == EntityType.MiscellaneousObjects))
                {
                    var match = false;
                    var lineColor = Color.White;
                    var lineText = "";
                    if (entity.HasComponent<Chest>() && entity.IsOpened) continue;
                    if (entity.HasComponent<Monster>() && (!entity.IsAlive || !entity.IsValid)) continue;
                    if (entity.GetHudComponent<SoundStatus>() != null && !entity.IsValid)
                        entity.GetHudComponent<SoundStatus>().Invalid();
                    if (entity.Type == EntityType.IngameIcon &&
                        (!entity.IsValid || (entity?.GetComponent<MinimapIcon>().IsHide ?? true))) continue;
                    var delta = entity.GridPos - GameController.Player.GridPos;
                    var distance = delta.GetPolarCoordinates(out var phi);

                    var rectDirection = new RectangleF(origin.X - margin - height / 2,
                        origin.Y - margin / 2 - height - lines * height, height, height);
                    var rectUV = Get64DirectionsUV(phi, distance, 3);
                    var ePath = entity.Path;
                    // prune paths where relevant
                    if (ePath.Contains("@")) ePath = ePath.Split('@')[0];
                    // Hud component check
                    var structValue = entity.GetHudComponent<ProximityAlert>();
                    if (structValue != null && !mods.Contains(structValue.Names))
                    {
                        mods += structValue.Names;
                        lines++;
                        var position = new Vector2(origin.X + height / 2, origin.Y - lines * height);
                        var textSize = Graphics.MeasureText(structValue.Names);
                        Graphics.DrawBox(position, position+textSize, Color.FromArgb(200, 0, 0, 0));
                        Graphics.DrawText(structValue.Names,
                            new Vector2(origin.X + height / 2, origin.Y - lines * height), structValue.Color);
                        Graphics.DrawImage("Direction-Arrow.png", rectDirection, rectUV, structValue.Color);
                        match = true;
                    }

                    // Contains Check
                    if (!match)
                        foreach (var filterEntry in _pathDict.Where(x => ePath.Contains(x.Key)).Take(1))
                        {
                            var filter = filterEntry.Value;
                            unopened = $"{filter.Text}\n{unopened}";
                            if (filter.Distance == -1 || filter.Distance == -2 && entity.IsValid ||
                                distance < filter.Distance)
                            {
                                var soundStatus = entity.GetHudComponent<SoundStatus>() ?? null;
                                if (soundStatus == null || !soundStatus.PlayedCheck())
                                    entity.SetHudComponent(new SoundStatus(entity, filter.SoundFile));
                                lineText = filter.Text;
                                lineColor = filter.Color;
                                match = true;
                                lines++;
                                break;
                            }
                        }

                    // Hardcoded Chests
                    if (!match)
                        if (entity.HasComponent<Chest>() && ePath.Contains("Delve"))
                        {
                            var chestName = Regex.Replace(Path.GetFileName(ePath),
                                    @"((?<=\p{Ll})\p{Lu})|((?!\A)\p{Lu}(?>\p{Ll}))", " $0")
                                .Replace("Delve Chest ", string.Empty)
                                .Replace("Delve Azurite ", "Azurite ")
                                .Replace("Delve Mining Supplies ", string.Empty)
                                .Replace("_", string.Empty);
                            if (chestName.EndsWith(" Encounter") || chestName.EndsWith(" No Drops")) continue;
                            if (distance > 100)
                                if (chestName.Contains("Generic")
                                    || chestName.Contains("Vein")
                                    || chestName.Contains("Flare")
                                    || chestName.Contains("Dynamite")
                                    || chestName.Contains("Armour")
                                    || chestName.Contains("Weapon"))
                                    if (chestName.Contains("Path ") || !chestName.Contains("Currency"))
                                        continue;
                            if (chestName.Contains("Currency") || chestName.Contains("Fossil"))
                                lineColor = Color.FromArgb(255, 255, 0, 255);
                            if (chestName.Contains("Flares")) lineColor = Color.FromArgb(255, 0, 200, 255);
                            if (chestName.Contains("Dynamite") || chestName.Contains("Explosives"))
                                lineColor = Color.FromArgb(255, 255, 50, 50);
                            lineText = chestName;
                            lines++;
                            match = true;
                        }

                    if (match)
                    {
                        var position = new Vector2(origin.X + height / 2, origin.Y - lines * height);
                        var textSize = Graphics.MeasureText(lineText);
                        Graphics.DrawBox(position, position+textSize, Color.FromArgb(200, 0, 0, 0));
                        Graphics.DrawText(lineText, new Vector2(origin.X + height / 2, origin.Y - lines * height),
                            lineColor);
                        Graphics.DrawImage("Direction-Arrow.png", rectDirection, rectUV, lineColor);
                    }
                }

                if (lines > 0)
                {
                    var widthMultiplier = 1 + height / 100;
                    var maxWidth = 192 * widthMultiplier;

                    // Find the maximum text width to determine box width
                    var allLines = mods.Split('\n');
                    foreach (var line in allLines)
                    {
                        var lineWidth = Graphics.MeasureText(line).X;
                        maxWidth = Math.Max(maxWidth, lineWidth + height + 4); // height + 4 for padding
                    }

                    var box = new RectangleF(
                        origin.X - 2,
                        origin.Y - margin - lines * height,
                        maxWidth + 4,
                        margin + lines * height + 4
                    );

                    // Draw top border line
                    Graphics.DrawLine(
                        new Vector2(origin.X - 15, origin.Y - margin - lines * height),
                        new Vector2(origin.X + maxWidth, origin.Y - margin - lines * height),
                        1,
                        Color.White
                    );

                    // Draw bottom border line
                    Graphics.DrawLine(
                        new Vector2(origin.X - 15, origin.Y + 3),
                        new Vector2(origin.X + maxWidth, origin.Y + 3),
                        1,
                        Color.White
                    );
                }
            }
            catch
            {
                // ignored
            }
        }


        private class Warning
        {
            public string Text { get; set; }
            public Color Color { get; set; }
            public int Distance { get; set; }
            public string SoundFile { get; set; }
        }

        private class SoundStatus
        {
            public SoundStatus(Entity entity, string sound)
            {
                this.Entity = entity;
                if (!Played && entity.IsValid)
                {
                    lock (Locker)
                    {
                        PlaySound(sound);
                    }

                    Played = true;
                }
            }

            private Entity Entity { get; }
            private bool Played { get; set; }

            public void Invalid()
            {
                if (Played && !Entity.IsValid) Played = false;
            }

            public bool PlayedCheck()
            {
                return Played;
            }
        }

        private class ProximityAlert
        {
            public ProximityAlert(Entity entity, List<Warning> warnings)
            {
                Entity = entity;
                Warnings = warnings;
                Names = string.Empty;
                Color = warnings[0].Color;
                PlayWarning = true;
            }

            private Entity Entity { get; }
            private List<Warning> Warnings { get; set; }
            public string Names { get; private set; }
            public Color Color { get; }
            private bool PlayWarning { get; set; }

            public void Update()
            {
                if (!Entity.IsValid) PlayWarning = true;
                if (!Entity.HasComponent<ObjectMagicProperties>() || !Entity.IsAlive) return;
                var mods = Entity.GetComponent<ObjectMagicProperties>()?.Mods;
                if (mods == null || mods.Count <= 0) return;

                var matchingMods = mods.Where(x => _modDict.ContainsKey(x)).ToList();
                if (!matchingMods.Any()) return;

                Warnings = matchingMods.Select(mod => _modDict[mod]).ToList();
                Names = string.Join("\n", Warnings.Select(w => w.Text));

                if (PlayWarning)
                {
                    lock (Locker)
                    {
                        PlaySound(Warnings[0].SoundFile);
                    }
                    PlayWarning = false;
                }
            }
        }

        // ReSharper disable once ClassNeverInstantiated.Local
        private class StaticEntity
        {
            public StaticEntity(string path, Vector3 pos)
            {
                Path = path;
                Pos = pos;
            }

            public string Path { get; }
            public Vector3 Pos { get; }
        }
    }
}