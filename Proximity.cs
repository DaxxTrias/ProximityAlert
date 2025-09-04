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
using System.Drawing;
using RectangleF = ExileCore2.Shared.RectangleF;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using System.Collections.Concurrent;

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
        private static readonly HashSet<StaticEntity> NearbyPaths = new HashSet<StaticEntity>();
        private readonly ConcurrentQueue<Entity> _entityAddedList = new ConcurrentQueue<Entity>();
        private IngameState _ingameState;
        private RectangleF _windowArea;
        private volatile List<Entity> _cachedMonsters;
        private Vector2 _cachedVector2 = new Vector2();
        private RectangleF _cachedRectangleF = new RectangleF();
        private Dictionary<string, Vector2> _cachedTextSizes = new Dictionary<string, Vector2>();
        private bool _hasArrowImage;
        private bool _hasBackImage;

        public override bool Initialise()
        {
            base.Initialise();
            Name = "Proximity Alerts";
            var gc = GameController;
            if (gc?.Game == null || gc.SoundController == null || gc.Window == null)
                return false;

            _ingameState = gc.Game.IngameState;
            lock (Locker) _soundController = gc.SoundController;
            _windowArea = gc.Window.GetWindowRectangle();

            var arrowPath = Path.Combine(DirectoryFullName, "textures", "Direction-Arrow.png");
            var backPath = Path.Combine(DirectoryFullName, "textures", "back.png");

            if (File.Exists(arrowPath))
            {
                Graphics.InitImage(arrowPath.Replace('\\', '/'), false);
                _hasArrowImage = true;
            }

            if (File.Exists(backPath))
            {
                Graphics.InitImage(backPath.Replace('\\', '/'), false);
                _hasBackImage = true;
            }

            lock (Locker) _soundDir = Path.Combine(DirectoryFullName, "sounds").Replace('\\', '/');
            var pathAlertsPath = EnsureConfigFile("PathAlerts.txt");
            var modAlertsPath = EnsureConfigFile("ModAlerts.txt");
            _pathDict = LoadConfig(pathAlertsPath);
            _modDict = LoadConfig(modAlertsPath);
            SetFonts();
            return true;
        }

        private static RectangleF Get64DirectionsUV(double phi, double distance, int rows)
        {
            phi += Math.PI * 0.25; // fix rotation due to projection
            if (phi > 2 * Math.PI) phi -= 2 * Math.PI;

            var xSprite = (float)Math.Round(phi / Math.PI * 32);
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
                line.Split(new[] { ';' }, 5).Select(parts => parts.Trim()).ToArray());
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

        private static bool TryHexToColor(string value, out Color color)
        {
            color = Color.White;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.StartsWith("#", StringComparison.Ordinal) ? value[1..] : value;
            if (v.Length == 6) v = "ff" + v;
            if (v.Length != 8) return false;

            if (!int.TryParse(v.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a)) return false;
            if (!int.TryParse(v.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)) return false;
            if (!int.TryParse(v.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)) return false;
            if (!int.TryParse(v.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return false;

            color = Color.FromArgb(a, r, g, b);
            return true;
        }

        private string EnsureConfigFile(string fileName)
        {
            try
            {
                var globalDir = ConfigDirectory;
                Directory.CreateDirectory(globalDir);
                var globalPath = Path.Combine(globalDir, fileName);
                var legacyPath = Path.Combine(DirectoryFullName, fileName);

                if (File.Exists(globalPath)) return globalPath;

                if (File.Exists(legacyPath))
                {
                    try { File.Copy(legacyPath, globalPath, false); } catch { /* ignore */ }
                    return globalPath;
                }

                // Create default with a helpful header
                File.WriteAllLines(globalPath, new[]
                {
                    "# ProximityAlert config",
                    "# Format: <PathContains> ; <Display Text> ; <AARRGGBB or RRGGBB> ; <Distance or -1> ; <SoundFileName>"
                });
                return globalPath;
            }
            catch
            {
                // Fallback to legacy location if something goes wrong
                return Path.Combine(DirectoryFullName, fileName);
            }
        }

        private Dictionary<string, Warning> LoadConfig(string path)
        {
            var dict = new Dictionary<string, Warning>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path)) return dict;
                foreach (var line in GenDictionary(path))
                {
                    if (line.Length < 5) continue;
                    var key = line[0];
                    var text = line[1];
                    var colorStr = line[2];
                    var distanceStr = line[3];
                    var sound = line[4];

                    if (!int.TryParse(distanceStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var distance))
                        distance = -1;

                    if (!TryHexToColor(colorStr, out var color))
                        color = Color.White;

                    dict[key] = new Warning { Text = text, Color = color, Distance = distance, SoundFile = sound };
                }
            }
            catch
            {
                // ignored - return what we have
            }
            return dict;
        }

        public override void EntityAdded(Entity entity)
        {
            if (!Settings.Enable.Value) return;
            if (entity.Type == EntityType.Monster) _entityAddedList.Enqueue(entity);
        }

        public override void AreaChange(AreaInstance area)
        {
            try
            {
                while (_entityAddedList.TryDequeue(out _)) { }
                NearbyPaths.Clear();
                _cachedTextSizes.Clear();
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
            var listWrapper = GameController?.EntityListWrapper;
            List<Entity> snapshot;
            try
            {
                if (listWrapper?.ValidEntitiesByType != null &&
                    listWrapper.ValidEntitiesByType.TryGetValue(EntityType.Monster, out var col) && col != null)
                {
                    snapshot = col.ToList();
                }
                else
                {
                    snapshot = new List<Entity>();
                }
            }
            catch
            {
                snapshot = new List<Entity>();
            }

            System.Threading.Volatile.Write(ref _cachedMonsters, snapshot);

            while (_entityAddedList.TryDequeue(out var entity) &&
                   !(GameController?.Area?.CurrentArea?.IsHideout == true || GameController?.Area?.CurrentArea?.IsTown == true))
            {
                if (entity == null) continue;
                if (entity.IsValid && !entity.IsAlive) continue;
                if (!entity.IsHostile || !entity.IsValid) continue;
                if (!Settings.ShowModAlerts.Value) continue;
                try
                {
                    if (entity.HasComponent<ObjectMagicProperties>() && entity.IsAlive)
                    {
                        var mods = entity.GetComponent<ObjectMagicProperties>()?.Mods;
                        if (mods is { Count: > 0 })
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
            foreach (var e in System.Threading.Volatile.Read(ref _cachedMonsters))
            {
                var drawCmd = e.GetHudComponent<ProximityAlert>();
                drawCmd?.Update();
            }
        }

        private static void PlaySound(string path)
        {
            lock (Locker)
            {
                if (!_playSounds) return;
                if (string.IsNullOrWhiteSpace(path)) return;
                if (_soundController == null || string.IsNullOrEmpty(_soundDir)) return;

                var now = DateTime.UtcNow;
                if ((now - _lastPlayed).TotalMilliseconds <= 250) return;

                try
                {
                    var full = Path.Combine(_soundDir, path).Replace('\\', '/');
                    if (!File.Exists(full)) return;
                    _soundController.PreloadSound(full);
                    _soundController.PlaySound(full);
                    _lastPlayed = now;
                }
                catch
                {
                    // shield audio driver issues
                }
            }
        }

        public override void Render()
        {
            try
            {
                var area = GameController?.Area?.CurrentArea;
                if (area == null || area.IsTown || area.IsHideout)
                    return;

                _playSounds = Settings.PlaySounds.Value;
                var scale = Math.Max(0.01f, Settings.Scale.Value);
                var fontStr = Settings.Font?.Value ?? "12";
                var digits = new string(fontStr.Where(char.IsDigit).ToArray());
                if (!float.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fontSize))
                    fontSize = 12f;
                var height = fontSize * scale;
                var margin = height / scale / 4f;

                if (!Settings.Enable.Value) return;
                if (_ingameState?.Camera == null || GameController?.Player == null) return;

                if (Settings.ShowPathAlerts.Value)
                    foreach (var sEnt in NearbyPaths)
                    {
                        if (sEnt == null) continue;
                        var entityScreenPos = _ingameState.Camera.WorldToScreen(sEnt.Pos);
                        var textWidth = GetCachedTextSize(sEnt.Path ?? string.Empty, 10) * 0.73f;
                        var position = new Vector2(entityScreenPos.X - textWidth.X / 2, entityScreenPos.Y - 7);
                        Graphics.DrawBox(position, position + new Vector2(textWidth.X, 13), Color.FromArgb(200, 0, 0, 0));
                        Graphics.DrawText(
                            sEnt.Path ?? string.Empty,
                            new Vector2(entityScreenPos.X, entityScreenPos.Y),
                            Color.White,
                            FontAlign.Center
                        );
                    }

                var shownModNames = new HashSet<string>(StringComparer.Ordinal);
                var lines = 0;

                var origin = _windowArea.Center.Translate(Settings.ProximityX.Value - 96, Settings.ProximityY.Value);

                // mod Alerts
                var monsters = System.Threading.Volatile.Read(ref _cachedMonsters);
                if (monsters != null)
                {
                    foreach (var entity in monsters)
                    {
                        if (entity.Rarity == MonsterRarity.White) continue;
                        var structValue = entity.GetHudComponent<ProximityAlert>();
                        if (structValue == null || !entity.IsAlive || structValue.Names == string.Empty) continue;
                        var delta = entity.GridPos - GameController.Player.GridPos;
                        var distance = delta.GetPolarCoordinates(out var phi);

                        var rectDirection = new RectangleF(origin.X - margin - height / 2,
                            origin.Y - margin / 2 - height - lines * height, height, height);
                        var rectUV = Get64DirectionsUV(phi, distance, 3);

                        if (shownModNames.Add(structValue.Names))
                        {
                            var modLines = structValue.Names.Split('\n');
                            var lineCount = modLines.Length;

                            for (var i = 0; i < lineCount; i++)
                            {
                                var currentLine = modLines[i];
                                var position = new Vector2(origin.X + height / 2, origin.Y - (lines + i + 1) * height);
                                var textSize = GetCachedTextSize(currentLine);

                                Graphics.DrawBox(
                                    position,
                                    position + new Vector2(textSize.X, height),
                                    Color.FromArgb(200, 0, 0, 0)
                                );

                                Graphics.DrawText(
                                    currentLine,
                                    position,
                                    structValue.Color
                                );
                            }

                            if (_hasArrowImage)
                                Graphics.DrawImage("Direction-Arrow.png", rectDirection, rectUV, structValue.Color);

                            lines += lineCount;
                        }
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
                    var miniIcon = entity.GetComponent<MinimapIcon>();
                    if (entity.Type == EntityType.IngameIcon &&
                        (!entity.IsValid || (miniIcon?.IsHide ?? true))) continue;
                    var delta = entity.GridPos - GameController.Player.GridPos;
                    var distance = delta.GetPolarCoordinates(out var phi);

                    var rectDirection = new RectangleF(origin.X - margin - height / 2,
                        origin.Y - margin / 2 - height - lines * height, height, height);
                    var rectUV = Get64DirectionsUV(phi, distance, 3);
                    var ePath = entity.Path ?? string.Empty;
                    if (ePath.Contains("@")) ePath = ePath.Split('@')[0];
                    var structValue = entity.GetHudComponent<ProximityAlert>();
                    if (structValue != null && shownModNames.Add(structValue.Names))
                    {
                        lines++;
                        var position = new Vector2(origin.X + height / 2, origin.Y - lines * height);
                        var textSize = GetCachedTextSize(structValue.Names);
                        Graphics.DrawBox(position, position + textSize, Color.FromArgb(200, 0, 0, 0));
                        Graphics.DrawText(structValue.Names,
                            new Vector2(origin.X + height / 2, origin.Y - lines * height), structValue.Color);
                        if (_hasArrowImage)
                            Graphics.DrawImage("Direction-Arrow.png", rectDirection, rectUV, structValue.Color);
                        match = true;
                    }

                    // Contains Check
                    if (!match)
                        foreach (var filterEntry in _pathDict.Where(x => ePath.Contains(x.Key)).Take(1))
                        {
                            var filter = filterEntry.Value;
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
                        var textSize = GetCachedTextSize(lineText);
                        Graphics.DrawBox(position, position + textSize, Color.FromArgb(200, 0, 0, 0));
                        Graphics.DrawText(lineText, new Vector2(origin.X + height / 2, origin.Y - lines * height),
                            lineColor);
                        if (_hasArrowImage)
                            Graphics.DrawImage("Direction-Arrow.png", rectDirection, rectUV, lineColor);
                    }
                }

                if (lines > 0)
                {
                    var widthMultiplier = 1 + height / 100;
                    var maxWidth = 192 * widthMultiplier;

                    var allLines = shownModNames.Count == 0 ? Array.Empty<string>() : shownModNames.SelectMany(n => n.Split('\n')).ToArray();
                    foreach (var line in allLines)
                    {
                        var lineWidth = GetCachedTextSize(line).X;
                        maxWidth = Math.Max(maxWidth, lineWidth + height + 4);
                    }

                    Graphics.DrawLine(
                        new Vector2(origin.X - 15, origin.Y - margin - lines * height),
                        new Vector2(origin.X + maxWidth, origin.Y - margin - lines * height),
                        1,
                        Color.White
                    );

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

        private Vector2 GetCachedTextSize(string text, int fontSize = 0)
        {
            var key = $"{text}_{fontSize}";
            if (!_cachedTextSizes.TryGetValue(key, out var size))
            {
                size = Graphics.MeasureText(text, fontSize);
                _cachedTextSizes[key] = size;
            }
            return size;
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

        public override void Dispose()
        {
            try
            {
                while (_entityAddedList.TryDequeue(out _)) { }
                NearbyPaths.Clear();
                _cachedTextSizes.Clear();
                System.Threading.Volatile.Write(ref _cachedMonsters, new List<Entity>());
                _pathDict.Clear();
                _modDict.Clear();
                lock (Locker)
                {
                    _soundController = null;
                    _soundDir = null;
                }
            }
            catch
            {
                // ignored
            }
            base.Dispose();
        }
    }
}
