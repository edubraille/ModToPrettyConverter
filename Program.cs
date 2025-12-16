/*
 * -----------------------------------------------------------------------------
 * Project:    ModToPrettyConverter (C# Port)
 * Version:    1.0 (KiCad 9 Compatible)
 * Date:       December 2025
 * 
 * Description:
 * Converter of older KiCad libraries (.mod) to modern format (.pretty). 
 * The program repairs file structures, groups elements, and adapts layers
 * (e.g., Reference/Value) to KiCad 9 standards.
 * The program requires some fine-tuning regarding arc conversion!
 * 
 * Author/Ported by: Sylwester Deja
 * 
 * Based on:
 * Original logic derived from legacy Python script "modToPretty.py" by NhatKhai (@nhatkhai).
 * Refactored and rewritten in C# with assistance from Google Gemini.
 * 
 * Copyright (c) 2025 Sylwester Deja
 * 
 * License:    MIT License
 * -----------------------------------------------------------------------------
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 * -----------------------------------------------------------------------------
 */


using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ModToPrettyConverter
{
    /// <summary>
    /// Klasa reprezentująca pojedynczy węzeł w strukturze pliku .mod.
    /// Pliki .mod mają strukturę drzewiastą (np. $MODULE zawiera w sobie $PAD i $SHAPE3D).
    /// </summary>
    class ModNode
    {
        // Nazwa bloku, np. "$MODULE", "$PAD"
        public string Name { get; set; }

        // Wskaźnik na rodzica (żeby wiedzieć, kiedy wyjść z zagnieżdżenia)
        public ModNode Parent { get; set; }

        // Lista podelementów (np. pady wewnątrz modułu)
        public List<ModNode> Children { get; set; } = new List<ModNode>();

        // Dane właściwe: Klucz (np. "Po", "T0") -> Lista parametrów
        public Dictionary<string, List<string[]>> Data { get; set; } = new Dictionary<string, List<string[]>>();

        public ModNode(string name, ModNode parent)
        {
            Name = name;
            Parent = parent;
        }

        // Dodaje linię danych do węzła
        public void AddData(string key, string[] items)
        {
            if (!Data.ContainsKey(key))
                Data[key] = new List<string[]>();
            Data[key].Add(items);
        }

        // Pobiera pierwszą linię danych dla danego klucza (pomocnicze)
        public string[] GetFirst(string key)
        {
            if (Data.ContainsKey(key) && Data[key].Count > 0)
                return Data[key][0];
            return null;
        }
    }

    class Program
    {
        // Przelicznik jednostek: 0.1 mil (decimil) na mm.
        // Stare pliki .mod zazwyczaj używają jednostek imperialnych.
        static double ModUnit = 2.54e-3;

        // Mapa warstw: Stary ID -> Nowa nazwa w KiCad
        static readonly Dictionary<int, string> LayerDic = new Dictionary<int, string>
        {
            { 0, "B.Cu" }, { 15, "F.Cu" }, { 16, "B.Adhes" }, { 17, "F.Adhes" },
            { 18, "B.Paste" }, { 19, "F.Paste" }, { 20, "B.SilkS" }, { 21, "F.SilkS" },
            { 22, "B.Mask" }, { 23, "F.Mask" }, { 24, "Dwgs.User" }, { 25, "Cmts.User" },
            { 26, "Eco1.User" }, { 27, "Eco2.User" }, { 28, "Edge.Cuts" }
        };

        static void Main(string[] args)
        {
            // --- 1. Konfiguracja i pobranie ścieżki ---
            string modDir = Directory.GetCurrentDirectory();

            if (args.Length >= 1)
            {
                modDir = args[0];
            }
            else
            {
                Console.WriteLine("----------------------------------------------------");
                Console.WriteLine("Konwerter .MOD -> .PRETTY (C# KiCad 9 Final)");
                Console.WriteLine("----------------------------------------------------");
                Console.Write($"Podaj sciezke do folderu z plikami .MOD\n[Wcisnij ENTER dla: {modDir}]: ");

                string input = Console.ReadLine();
                // Usuwamy cudzysłowy, które Windows dodaje przy przeciąganiu folderu ("C:\...")
                if (!string.IsNullOrWhiteSpace(input)) modDir = input.Trim().Replace("\"", "");
            }

            if (string.IsNullOrEmpty(modDir) || !Directory.Exists(modDir))
            {
                Console.WriteLine($"Blad: Katalog nie istnieje: '{modDir}'");
                Console.ReadKey();
                return;
            }

            // --- 2. Wyszukiwanie i przetwarzanie plików ---
            try
            {
                string[] files = Directory.GetFiles(modDir, "*.mod");
                if (files.Length == 0)
                {
                    Console.WriteLine("Brak plikow .mod w podanym katalogu.");
                }
                else
                {
                    foreach (var modFile in files)
                    {
                        Console.WriteLine("----------------------------------------------------");
                        Console.WriteLine("Przetwarzanie pliku: " + Path.GetFileName(modFile));

                        // Tworzymy folder .pretty w tym samym miejscu co plik .mod
                        string prettyDir = Path.Combine(Path.GetDirectoryName(modFile), Path.GetFileName(modFile) + ".pretty");

                        ConvertModToPretty(modFile, prettyDir);

                        Console.WriteLine("Zapisano biblioteke w: " + prettyDir);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Wystapil krytyczny blad: " + ex.Message);
            }

            Console.WriteLine("\nGotowe. Nacisnij dowolny klawisz, aby zamknac.");
            Console.ReadKey();
        }

        /// <summary>
        /// Główna pętla parsująca plik .mod linia po linii.
        /// Buduje strukturę drzewiastą (ModNode) i wywołuje generator przy końcu modułu.
        /// </summary>
        static void ConvertModToPretty(string modFile, string prettyDir)
        {
            if (!Directory.Exists(prettyDir)) Directory.CreateDirectory(prettyDir);

            // Reset jednostki dla każdego pliku (domyślnie imperialne)
            ModUnit = 2.54e-3;

            int lineCnt = 0;
            ModNode root = new ModNode("root", null);
            ModNode current = root;

            // Używamy kodowania Windows-1252, typowego dla starszych plików tekstowych
            foreach (string line in File.ReadLines(modFile, Encoding.GetEncoding(1252)))
            {
                lineCnt++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Dzielimy linię na słowa, ignorując puste wpisy (wielokrotne spacje)
                string[] items = line.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (items.Length == 0) continue;

                string key = items[0];

                // Wykrycie końca modułu -> Generowanie pliku .kicad_mod
                if (key.StartsWith("$EndMODULE"))
                {
                    ProcessModuleData(current, prettyDir, Path.GetFileName(modFile), lineCnt);
                    // Reset drzewa po zapisaniu, powrót do korzenia
                    current = root;
                    root.Children.Clear();
                }
                // Wyjście z bloku (np. koniec $PAD)
                else if (key.StartsWith("$End"))
                {
                    if (current.Parent != null) current = current.Parent;
                }
                // Wejście do nowego bloku (np. $MODULE, $PAD)
                else if (key.StartsWith("$"))
                {
                    ModNode newNode = new ModNode(key, current);
                    current.Children.Add(newNode);
                    current = newNode;
                    // Dodajemy linię nagłówka (np. "$MODULE NAZWA") do danych węzła, żeby znać nazwę
                    current.AddData(key, items);
                }
                else
                {
                    // --- AGREGACJA DANYCH (Poprawka logiczna) ---
                    // Grupowanie wszystkich tekstów (T0, T1, T2...) pod kluczem "T"
                    // Grupowanie wszystkich rysunków (DS, DC, DA...) pod kluczem "D"
                    if (key.StartsWith("T")) current.AddData("T", items);
                    else if (key.StartsWith("D")) current.AddData("D", items);
                    else current.AddData(key, items);
                }
            }
        }

        /// <summary>
        /// Generuje plik .kicad_mod na podstawie zebranych danych (ModNode).
        /// </summary>
        static void ProcessModuleData(ModNode node, string prettyDir, string modFileName, int lineCnt)
        {
            // 1. Sprawdzenie jednostek w pliku nadrzędnym (Units mm/in)
            if (node.Parent != null)
            {
                string[] units = node.Parent.GetFirst("Units");
                if (units != null && units.Length > 1 && units[1].ToLower() == "mm") ModUnit = 1.0;
            }

            // 2. Pobranie nazwy modułu
            string[] modHeader = node.GetFirst("$MODULE");
            string modName = "Unknown";
            if (modHeader != null && modHeader.Length > 1)
            {
                // Scalamy nazwę, jeśli miała spacje (chociaż split to rozdzielił)
                modName = string.Join(" ", modHeader.Skip(1));
                // Usuwamy cudzysłowy, jeśli są w nazwie
                if (modName.StartsWith("\"") && modName.EndsWith("\"")) modName = modName.Replace("\"", "");
            }

            Console.WriteLine($"*** Przetwarzanie [{lineCnt,5}] modulu: {modName} ***");

            // 3. Pozycjonowanie i atrybuty główne (Po)
            // Format Po: Po X Y Ori Layer Tedit Tstamp ...
            string[] poData = node.GetFirst("Po");
            if (poData == null) poData = new string[] { "Po", "0", "0", "0", "15", "0", "0", "~~" };

            // Ustalanie warstwy (indeksy przesunięte przez RemoveEmptyEntries)
            string layerName = "F.Cu";
            int layerIdx = 4;
            // Próba pobrania ID warstwy i konwersji na nazwę KiCad
            if (poData.Length > layerIdx && int.TryParse(poData[layerIdx], out int lid) && LayerDic.ContainsKey(lid))
                layerName = LayerDic[lid];
            else if (poData.Length > 3 && int.TryParse(poData[3], out int lid3) && LayerDic.ContainsKey(lid3))
                layerName = LayerDic[lid3]; // Fallback

            string tedit = (poData.Length > 5) ? poData[5] : ((poData.Length > 4) ? poData[4] : "0");

            // Flagi Locked/Placed
            string locked = "", placed = "";
            int statusIdx = 6;
            if (poData.Length > statusIdx)
            {
                if (poData[statusIdx].Length > 0 && poData[statusIdx][0] == 'F') locked = "locked ";
                if (poData[statusIdx].Length > 1 && poData[statusIdx][1] == 'P') placed = "placed ";
            }

            // Opisy, słowa kluczowe, atrybuty
            string[] cdData = node.GetFirst("Cd");
            string descr = (cdData != null) ? GenText(string.Join(" ", cdData.Skip(1))) : "";
            string[] kwData = node.GetFirst("Kw");
            string tags = (kwData != null) ? GenText(string.Join(" ", kwData.Skip(1))) : "";
            string[] atData = node.GetFirst("At");
            string attr = (atData != null && atData.Length > 1) ? atData[1].ToLower() : "";
            double yMax = 0; // Do obliczenia pozycji tekstu ${REFERENCE}

            // Przygotowanie nazwy pliku (usuwanie znaków niedozwolonych)
            string safeModName = modName;
            foreach (char c in Path.GetInvalidFileNameChars()) safeModName = safeModName.Replace(c, '_');
            string prettyFile = Path.Combine(prettyDir, safeModName + ".kicad_mod");

            // --- ROZPOCZĘCIE ZAPISU PLIKU ---
            using (StreamWriter sw = new StreamWriter(prettyFile))
            {
                sw.WriteLine($"(module {GenText(modName)} {locked}{placed}(layer {layerName}) (tedit {tedit})");
                if (!string.IsNullOrEmpty(descr)) sw.WriteLine($"  (descr {descr})");
                if (!string.IsNullOrEmpty(tags)) sw.WriteLine($"  (tags {tags})");
                if (!string.IsNullOrEmpty(attr)) sw.WriteLine($"  (attr {attr})");

                // --- SEKJA: TEKSTY (T0, T1...) ---
                if (node.Data.ContainsKey("T"))
                {
                    foreach (var txt in node.Data["T"])
                    {
                        try
                        {
                            if (txt == null || txt.Length < 1) continue;

                            // Wyciąganie etykiety tekstowej (od indeksu 11 do końca)
                            string label = "";
                            if (txt.Length > 11)
                            {
                                label = string.Join(" ", txt.Skip(11));
                                if (label.StartsWith("\"") && label.EndsWith("\"") && label.Length >= 2)
                                    label = label.Substring(1, label.Length - 2);
                            }

                            string type = "user";
                            if (txt[0] == "T0") { type = "reference"; if (string.IsNullOrEmpty(label)) label = "Ref**"; }
                            else if (txt[0] == "T1") { type = "value"; if (string.IsNullOrEmpty(label)) label = "Val**"; }

                            // Widoczność (I = Invisible -> hide)
                            string visible = "";
                            if (type == "user" && txt.Length > 8 && txt[8] == "I") visible = " hide";

                            string tLayer = "F.SilkS";
                            if (txt.Length > 9 && int.TryParse(txt[9], out int tlId) && LayerDic.ContainsKey(tlId))
                                tLayer = LayerDic[tlId];

                            // *** POPRAWKA DLA KICAD 9: Wartości (Value) zawsze na F.Fab ***
                            if (type == "value") tLayer = "F.Fab";

                            string at = GenAT(txt[1], txt[2], txt.Length > 5 ? txt[5] : "0");

                            sw.WriteLine($"  (fp_text {type} {GenText(label)} (at {at}) (layer {tLayer}){visible}");
                            sw.WriteLine($"    (effects (font (size {GenNum(txt[3])} {GenNum(txt[4])}) (thickness {GenNum(txt[6])})))");
                            sw.WriteLine("  )");

                            // Aktualizacja maksymalnej wysokości (do pozycjonowania opisu pod spodem)
                            if (TryParseDouble(txt[2], out double y)) if (y > yMax) yMax = y;
                        }
                        catch { /* Ignorowanie uszkodzonych linii */ }
                    }
                }

                // --- SEKCJA: RYSUNKI (Lines, Circles, Arcs) ---
                if (node.Data.ContainsKey("D"))
                {
                    int dpCount = 0; string dpPen = "", dpLayer = ""; bool inPoly = false;
                    foreach (var d in node.Data["D"])
                    {
                        try
                        {
                            if (d == null || d.Length < 1) continue;

                            // DS - Line Segment
                            if (d[0] == "DS" && d.Length >= 7)
                                sw.WriteLine($"  (fp_line (start {GenNum(d[1])} {GenNum(d[2])}) (end {GenNum(d[3])} {GenNum(d[4])}) (layer {GetLayer(d[6])}) (width {GenNum(d[5])}))");
                            // DC - Circle
                            else if (d[0] == "DC" && d.Length >= 7)
                                sw.WriteLine($"  (fp_circle (center {GenNum(d[1])} {GenNum(d[2])}) (end {GenNum(d[3])} {GenNum(d[4])}) (layer {GetLayer(d[6])}) (width {GenNum(d[5])}))");
                            // DA - Arc
                            else if (d[0] == "DA" && d.Length >= 8)
                                sw.WriteLine($"  (fp_arc (start {GenNum(d[1])} {GenNum(d[2])}) (end {GenNum(d[3])} {GenNum(d[4])}) (angle {d[5]}) (layer {GetLayer(d[7])}) (width {GenNum(d[6])}))");
                            // DP/Dl - Polygons
                            else if (d[0] == "DP" && d.Length >= 8)
                            {
                                int.TryParse(d[5], out dpCount); dpPen = GenNum(d[6]); dpLayer = GetLayer(d[7]);
                                sw.Write("  (fp_poly (pts"); inPoly = true;
                            }
                            else if (d[0] == "Dl" && d.Length >= 3)
                            {
                                dpCount--;
                                if (dpCount >= 0) { sw.Write($" (xy {GenNum(d[1])} {GenNum(d[2])})"); if (dpCount % 4 == 3) sw.Write("\n   "); }
                                if (dpCount == 0 && inPoly) { sw.WriteLine(")"); sw.WriteLine($"    (layer {dpLayer}) (width {dpPen})"); sw.WriteLine("  )"); inPoly = false; }
                            }

                            // Sprawdzanie Y max dla rysunków
                            if (d.Length > 2 && TryParseDouble(d[2], out double y1) && y1 > yMax) yMax = y1;
                            if (d.Length > 4 && d[0] != "DA" && TryParseDouble(d[4], out double y2) && y2 > yMax) yMax = y2;
                        }
                        catch { }
                    }
                }

                // --- SEKCJA: PADY i MODELE 3D (Zagnieżdżone) ---
                foreach (var child in node.Children)
                {
                    if (child.Name == "$PAD")
                    {
                        try
                        {
                            var dic = child.Data;
                            // Atrybuty Padów (At)
                            string[] At = dic.ContainsKey("At") ? dic["At"][0] : new string[] { "", "", "0", "0" };

                            string atType = (At.Length > 1) ? At[1].ToLower() : "std";

                            // *** POPRAWKA INDEKSOWANIA ***
                            // Maska warstwy jest na indeksie 3 (po usunięciu pustych stringów)
                            string maskVal = (At.Length > 3) ? At[3] : "0";

                            string kind = "", layers = "";
                            if (atType == "std") { kind = "thru_hole"; layers = $" (layers *.Cu {GetLayersMask(maskVal, 0xFFFF0000)})"; }
                            else if (atType == "smd") { kind = "smd"; layers = $" (layers {GetLayersMask(maskVal)})"; }

                            // Kształt (Sh)
                            string[] Sh = dic.ContainsKey("Sh") ? dic["Sh"][0] : new string[] { };

                            // *** POPRAWKA NAZWY PADA ***
                            // Jeśli nazwa jest pusta, GenText musi zwrócić "", dlatego tutaj usuwamy tylko cudzysłowy
                            string padName = (Sh.Length > 1) ? Sh[1].Replace("\"", "") : "";

                            string shapeCode = (Sh.Length > 2) ? Sh[2] : "C";
                            string W = (Sh.Length > 3) ? GenNum(Sh[3]) : "0";
                            string H = (Sh.Length > 4) ? GenNum(Sh[4]) : "0";
                            string rot = (Sh.Length > 7) ? Sh[7] : "0";

                            // Pozycja
                            string[] Po = dic.ContainsKey("Po") ? dic["Po"][0] : new string[] { "Po", "0", "0" };
                            string shapeStr = "";
                            string atStr = GenAT(Po[1], Po[2], rot);

                            if (shapeCode == "R") shapeStr = $"rect (at {atStr}) (size {W} {H})";
                            else if (shapeCode == "C") shapeStr = $"circle (at {GenNum(Po[1])} {GenNum(Po[2])}) (size {W} {H})";
                            else if (shapeCode == "O") shapeStr = $"oval (at {atStr}) (size {W} {H})";

                            // Otwory (Drill)
                            string drillStr = "";
                            List<string> drills = new List<string>();
                            if (dic.ContainsKey("D"))
                            {
                                foreach (var d in dic["D"])
                                    if (d != null && d.Length > 1 && d[0] == "Dr" && TryParseDouble(d[1], out double sz) && sz != 0)
                                    {
                                        if (d.Length >= 4) drills.Add($"(drill {GenNum(d[1])}{GenOfs(d[2], d[3])})");
                                        else if (d.Length >= 7 && d[4] == "O") drills.Add($"(drill oval {GenNum(d[1])} {GenOfs(d[2], d[3])} {GenNum(d[5])} {GenNum(d[6])})");
                                    }
                            }
                            if (drills.Count > 0) drillStr = " " + string.Join(" ", drills);

                            sw.WriteLine($"  (pad {GenText(padName)} {kind} {shapeStr}{drillStr}{layers})");

                            if (TryParseDouble(Po[2], out double y) && y > yMax) yMax = y;
                        }
                        catch { }
                    }
                    else if (child.Name == "$SHAPE3D")
                    {
                        try
                        {
                            var dic = child.Data;
                            string[] Na = dic.ContainsKey("Na") ? dic["Na"][0] : null;
                            string[] Of = dic.ContainsKey("Of") ? dic["Of"][0] : new string[] { "", "0", "0", "0" };
                            string[] Sc = dic.ContainsKey("Sc") ? dic["Sc"][0] : new string[] { "", "1", "1", "1" };
                            string[] Ro = dic.ContainsKey("Ro") ? dic["Ro"][0] : new string[] { "", "0", "0", "0" };
                            if (Na != null && Na.Length > 1)
                            {
                                sw.WriteLine($"  (model {GenText(Na[1].Replace("\"", ""))}");
                                sw.WriteLine($"    (at (xyz {GenNum(Of[1])} {GenNum(Of[2])} {GenNum(Of[3])}))");
                                sw.WriteLine($"    (scale (xyz {string.Join(" ", Sc.Skip(1))}))");
                                sw.WriteLine($"    (rotate (xyz {string.Join(" ", Ro.Skip(1))}))");
                                sw.WriteLine("  )");
                            }
                        }
                        catch { }
                    }
                }

                // Dodanie tekstu ${REFERENCE} na warstwie F.Fab (wymagane dla bibliotek KiCad 9+)
                double textY = yMax * ModUnit + 1.0;
                string textYStr = textY.ToString("0.######", CultureInfo.InvariantCulture);
                sw.WriteLine($"  (fp_text user \"${{REFERENCE}}\" (at 0 {textYStr}) (layer F.Fab)");
                sw.WriteLine("    (effects (font (size 0.4 0.4) (thickness 0.1)))");
                sw.WriteLine("  )");

                sw.WriteLine(")"); // Zamknięcie modułu
            }
        }

        // --- Metody pomocnicze ---

        // Konwersja liczby (jednostki)
        static string GenNum(string text) { return TryParseDouble(text, out double val) ? (val * ModUnit).ToString("0.######", CultureInfo.InvariantCulture) : "0"; }

        static bool TryParseDouble(string text, out double result) { return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out result); }

        // Formatowanie tekstu (dodawanie cudzysłowów dla KiCad)
        static string GenText(string txt)
        {
            // *** POPRAWKA: Puste stringi muszą być jako "" w KiCad
            if (string.IsNullOrEmpty(txt)) return "\"\"";
            return (txt.Contains(" ") || txt.Contains("(") || txt.Contains(")")) ? "\"" + txt.Replace("\"", "") + "\"" : txt;
        }

        // Formatowanie współrzędnych i kąta (at x y a)
        static string GenAT(string x, string y, string a)
        {
            TryParseDouble(x, out double dx); TryParseDouble(y, out double dy); TryParseDouble(a, out double da);
            string angleStr = (Math.Abs(da / 10.0) > 0.001) ? " " + (da / 10.0).ToString("0.###", CultureInfo.InvariantCulture) : "";
            return $"{GenNum(x)} {GenNum(y)}{angleStr}";
        }

        // Formatowanie offsetu dla wiercenia
        static string GenOfs(string x, string y)
        {
            TryParseDouble(x, out double dx); TryParseDouble(y, out double dy);
            return (dx == 0 && dy == 0) ? "" : $" (offset {GenNum(x)} {GenNum(y)})";
        }

        // Pobieranie nazwy warstwy z ID
        static string GetLayer(string layerIdStr) { return (int.TryParse(layerIdStr, out int id) && LayerDic.ContainsKey(id)) ? LayerDic[id] : "F.SilkS"; }

        // Dekodowanie maski bitowej warstw
        static string GetLayersMask(string bitMaskStr, uint testMask = 0xFFFFFFFF)
        {
            List<string> layers = new List<string>();
            try
            {
                long bitMask = 0;
                if (bitMaskStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) bitMask = Convert.ToInt64(bitMaskStr.Substring(2), 16);
                else long.TryParse(bitMaskStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bitMask);
                bitMask &= testMask;
                int i = 0;
                while (bitMask != 0) { if ((bitMask & 1) != 0 && LayerDic.ContainsKey(i)) layers.Add(LayerDic[i]); bitMask >>= 1; i++; }
            }
            catch { }
            return string.Join(" ", layers);
        }
    }
}