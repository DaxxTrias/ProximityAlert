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
using System.Text;

// ReSharper disable CollectionNeverUpdated.Local

namespace ProximityAlert
{
    public partial class Proximity : BaseSettingsPlugin<ProximitySettings>
    {
        private static SoundController _soundController;
        private Dictionary<string, Warning> _pathDict = new Dictionary<string, Warning>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, Warning> _modDict = new Dictionary<string, Warning>(StringComparer.OrdinalIgnoreCase);
        private static string _soundDir;
        private static bool _playSounds = true;
        private static DateTime _lastPlayed;
        private static readonly object Locker = new object();
        private readonly HashSet<StaticEntity> NearbyPaths = new HashSet<StaticEntity>();
        private readonly ConcurrentQueue<Entity> _entityAddedList = new ConcurrentQueue<Entity>();
        private IngameState _ingameState;
        private RectangleF _windowArea;
        private volatile List<Entity> _cachedMonsters;
        private readonly Dictionary<string, Vector2> _cachedTextSizes = new Dictionary<string, Vector2>();
        private readonly object _textSizeCacheLock = new object();
        private readonly Queue<string> _textSizeKeys = new Queue<string>();
        private const int TextSizeCacheCapacity = 192;
        private bool _hasArrowImage;
        private bool _hasBackImage;
        // Reuse sets/caches to reduce per-frame allocations
        private readonly HashSet<string> _shownModNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _splitCache = new Dictionary<string, string[]>();
        private readonly Queue<string> _splitCacheKeys = new Queue<string>();
        private const int SplitCacheCapacity = 128;
        private readonly Dictionary<string, string> _chestNameCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Queue<string> _chestNameKeys = new Queue<string>();
        private const int ChestNameCacheCapacity = 256;
        private static readonly Regex ChestNameRegex = new Regex(@"((?<=\p{Ll})\p{Lu})|((?!\A)\p{Lu}(?>\p{Ll}))", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // Double-buffer monster snapshots to avoid allocations
        private readonly List<Entity> _monstersBufferA = new List<Entity>(128);
        private readonly List<Entity> _monstersBufferB = new List<Entity>(128);
        private bool _useBufferA;
        // Cached font/scale derived values
        private string _lastFontRaw;
        private float _lastScale = 1f;
        private float _cachedFontSize = 12f;
        private float _cachedHeight = 12f;
        private float _cachedMargin = 3f;

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
                lock (_textSizeCacheLock)
                {
                    _cachedTextSizes.Clear();
                    _textSizeKeys.Clear();
                }
                // Clear small LRU caches on area change
                _splitCache.Clear();
                _splitCacheKeys.Clear();
                _chestNameCache.Clear();
                _chestNameKeys.Clear();
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
            List<Entity> buffer;
            try
            {
                buffer = _useBufferA ? _monstersBufferA : _monstersBufferB;
                buffer.Clear();

                if (listWrapper?.ValidEntitiesByType != null &&
                    listWrapper.ValidEntitiesByType.TryGetValue(EntityType.Monster, out var col) && col != null)
                {
                    foreach (var e in col)
                        buffer.Add(e);
                }
            }
            catch
            {
                buffer = _useBufferA ? _monstersBufferA : _monstersBufferB;
                buffer.Clear();
            }

            System.Threading.Volatile.Write(ref _cachedMonsters, buffer);
            _useBufferA = !_useBufferA;

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
                            var filters = new List<Warning>(mods.Count);
                            foreach (var mod in mods)
                            {
                                if (_modDict.TryGetValue(mod, out var warn))
                                    filters.Add(warn);
                            }
                            if (filters.Count > 0)
                            {
                                entity.SetHudComponent(new ProximityAlert(this, entity, filters));
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
            var monsters = System.Threading.Volatile.Read(ref _cachedMonsters);
            if (monsters != null)
            {
                foreach (var e in monsters)
                {
                    var drawCmd = e.GetHudComponent<ProximityAlert>();
                    drawCmd?.Update();
                }
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
                if (!string.Equals(fontStr, _lastFontRaw, StringComparison.Ordinal) || Math.Abs(scale - _lastScale) > float.Epsilon)
                {
                    int acc = 0;
                    for (int i = 0; i < fontStr.Length; i++)
                    {
                        var c = fontStr[i];
                        if (c >= '0' && c <= '9')
                        {
                            acc = (acc * 10) + (c - '0');
                        }
                    }
                    _cachedFontSize = acc <= 0 ? 12f : acc;
                    _cachedHeight = _cachedFontSize * scale;
                    _cachedMargin = _cachedHeight / scale / 4f;
                    _lastFontRaw = fontStr;
                    _lastScale = scale;
                }

                var height = _cachedHeight;
                var margin = _cachedMargin;

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

                _shownModNames.Clear();
                var lines = 0;
                float maxLineWidth = 0f;

                var origin = _windowArea.Center.Translate(Settings.ProximityX.Value - 96, Settings.ProximityY.Value);

                // Defer drawing of text/arrows so borders are drawn first
                var textBoxes = new List<(Vector2 Pos, Vector2 Size, string Text, Color Color)>(64);
                var arrowDraws = new List<(RectangleF Rect, RectangleF UV, Color Color)>(32);

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

                        if (_shownModNames.Add(structValue.Names))
                        {
                            var modLines = GetCachedSplitLines(structValue.Names);
                            var lineCount = modLines.Length;

                            for (var i = 0; i < lineCount; i++)
                            {
                                var currentLine = modLines[i];
                                var position = new Vector2(origin.X + height / 2, origin.Y - (lines + i + 1) * height);
                                var textSize = GetCachedTextSize(currentLine);
                                if (textSize.X > maxLineWidth) maxLineWidth = textSize.X;

                                textBoxes.Add((position, new Vector2(textSize.X, height), currentLine, structValue.Color));
                            }

                            if (_hasArrowImage)
                                arrowDraws.Add((rectDirection, rectUV, structValue.Color));

                            lines += lineCount;
                        }
                    }
                }

                // entities
                var allEntities = GameController.EntityListWrapper.Entities;
                foreach (var entity in allEntities)
                {
                    var type = entity.Type;
                    if (!(type == EntityType.Chest || type == EntityType.Monster || type == EntityType.IngameIcon || type == EntityType.MiscellaneousObjects))
                        continue;

                    var match = false;
                    var lineColor = Color.White;
                    var lineText = "";

                    if (entity.HasComponent<Chest>() && entity.IsOpened) continue;
                    if (entity.HasComponent<Monster>() && (!entity.IsAlive || !entity.IsValid)) continue;

                    if (type == EntityType.IngameIcon)
                    {
                        var miniIcon = entity.GetComponent<MinimapIcon>();
                        if (!entity.IsValid || (miniIcon?.IsHide ?? true)) continue;
                    }

                    var delta = entity.GridPos - GameController.Player.GridPos;
                    var distance = delta.GetPolarCoordinates(out var phi2);

                    var rectDirection2 = new RectangleF(origin.X - margin - height / 2,
                        origin.Y - margin / 2 - height - lines * height, height, height);
                    var rectUV2 = Get64DirectionsUV(phi2, distance, 3);
                    var ePath = entity.Path ?? string.Empty;
                    if (ePath.Contains("@")) ePath = ePath.Split('@')[0];
                    var structValue2 = entity.GetHudComponent<ProximityAlert>();
                    if (structValue2 != null && _shownModNames.Add(structValue2.Names))
                    {
                        lines++;
                        var position = new Vector2(origin.X + height / 2, origin.Y - lines * height);
                        var textSize = GetCachedTextSize(structValue2.Names);
                        if (textSize.X > maxLineWidth) maxLineWidth = textSize.X;
                        textBoxes.Add((position, textSize, structValue2.Names, structValue2.Color));
                        if (_hasArrowImage)
                            arrowDraws.Add((rectDirection2, rectUV2, structValue2.Color));
                        match = true;
                    }

                    // Contains Check
                    if (!match)
                    {
                        foreach (var kvp in _pathDict)
                        {
                            if (ePath.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var filter = kvp.Value;
                                if (filter.Distance == -1 || filter.Distance == -2 && entity.IsValid ||
                                    distance < filter.Distance)
                                {
                                    var soundStatus = entity.GetHudComponent<SoundStatus>();
                                    if (soundStatus == null || !soundStatus.PlayedCheck())
                                        entity.SetHudComponent(new SoundStatus(entity, filter.SoundFile));
                                    lineText = filter.Text;
                                    lineColor = filter.Color;
                                    match = true;
                                    lines++;
                                }
                                break;
                            }
                        }
                    }

                    // Hardcoded Chests
                    if (!match)
                        if (entity.HasComponent<Chest>() && ePath.Contains("Delve"))
                        {
                            var chestName = GetCachedChestName(ePath);
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
                        if (textSize.X > maxLineWidth) maxLineWidth = textSize.X;
                        textBoxes.Add((position, textSize, lineText, lineColor));
                        if (_hasArrowImage)
                            arrowDraws.Add((rectDirection2, rectUV2, lineColor));
                    }
                }

                if (lines > 0)
                {
                    var widthMultiplier = 1 + height / 100;
                    var maxWidth = Math.Max(192 * widthMultiplier, maxLineWidth + height + 4);

                    // draw borders/background first so they don't overlap text
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

                    // then draw deferred arrows and text on top
                    if (_hasArrowImage)
                    {
                        for (int i = 0; i < arrowDraws.Count; i++)
                        {
                            var a = arrowDraws[i];
                            Graphics.DrawImage("Direction-Arrow.png", a.Rect, a.UV, a.Color);
                        }
                    }

                    for (int i = 0; i < textBoxes.Count; i++)
                    {
                        var t = textBoxes[i];
                        Graphics.DrawBox(t.Pos, t.Pos + t.Size, Color.FromArgb(200, 0, 0, 0));
                        Graphics.DrawText(t.Text, t.Pos, t.Color);
                    }
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
            lock (_textSizeCacheLock)
            {
                if (!_cachedTextSizes.TryGetValue(key, out var size))
                {
                    size = Graphics.MeasureText(text, fontSize);
                    _cachedTextSizes[key] = size;
                    _textSizeKeys.Enqueue(key);
                    if (_textSizeKeys.Count > TextSizeCacheCapacity)
                    {
                        var oldKey = _textSizeKeys.Dequeue();
                        _cachedTextSizes.Remove(oldKey);
                    }
                }
                return size;
            }
        }

        private string[] GetCachedSplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            if (_splitCache.TryGetValue(text, out var lines)) return lines;
            var arr = text.Split('\n');
            _splitCache[text] = arr;
            _splitCacheKeys.Enqueue(text);
            if (_splitCacheKeys.Count > SplitCacheCapacity)
            {
                var oldKey = _splitCacheKeys.Dequeue();
                _splitCache.Remove(oldKey);
            }
            return arr;
        }

        private string GetCachedChestName(string ePath)
        {
            if (_chestNameCache.TryGetValue(ePath, out var value)) return value;
            var chestName = ChestNameRegex.Replace(Path.GetFileName(ePath), " $0")
                .Replace("Delve Chest ", string.Empty)
                .Replace("Delve Azurite ", "Azurite ")
                .Replace("Delve Mining Supplies ", string.Empty)
                .Replace("_", string.Empty);
            _chestNameCache[ePath] = chestName;
            _chestNameKeys.Enqueue(ePath);
            if (_chestNameKeys.Count > ChestNameCacheCapacity)
            {
                var oldKey = _chestNameKeys.Dequeue();
                _chestNameCache.Remove(oldKey);
            }
            return chestName;
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
            public ProximityAlert(Proximity owner, Entity entity, List<Warning> warnings)
            {
                Owner = owner;
                Entity = entity;
                Warnings = warnings;
                Names = string.Empty;
                Color = warnings[0].Color;
                PlayWarning = true;
            }

            private Proximity Owner { get; }
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

                List<Warning> newWarnings = null;
                foreach (var mod in mods)
                {
                    if (Owner._modDict.TryGetValue(mod, out var warn))
                    {
                        newWarnings ??= new List<Warning>(mods.Count);
                        newWarnings.Add(warn);
                    }
                }
                if (newWarnings == null || newWarnings.Count == 0) return;

                Warnings = newWarnings;
                var sb = new StringBuilder();
                for (int i = 0; i < newWarnings.Count; i++)
                {
                    if (i > 0) sb.Append('\n');
                    sb.Append(newWarnings[i].Text);
                }
                Names = sb.ToString();

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
                lock (_textSizeCacheLock)
                {
                    _cachedTextSizes.Clear();
                    _textSizeKeys.Clear();
                }
                _splitCache.Clear();
                _splitCacheKeys.Clear();
                _chestNameCache.Clear();
                _chestNameKeys.Clear();
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
