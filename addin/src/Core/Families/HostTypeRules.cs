// New in Plinth: the Revit-free decisions behind ensure_wall_type and ensure_floor_type. Drawings give walls and slabs by
// THICKNESS (250 mm outer wall, 125 mm inner wall, 150 mm slab), so the build needs a Basic Wall / Floor type with exactly
// that total thickness, without ever editing an existing type (duplicate before edit, see SharedEditRule). The Revit side
// (addin/src/Revit/Commands/Families/HostTypes.cs) reads the facts (candidate types, their layer stacks, function,
// structural layer) and applies these decisions.
//
// Candidates: walls - every Basic Wall type (never curtain or stacked); floors - every FloorType whose IsFoundationSlab
// equals the request's foundation flag. Other types of the same class only matter for their NAMES (a new type needs a
// free name).
//
// HostTypeRule.Decide, in order (FIND, then ADAPT A COPY, then ASK):
//   1. by name   - the requested name (name, else the default "Plinth <t>mm Exterior|Interior" / "Plinth <t>mm Slab"): a
//                  candidate of that name is returned when it matches (matched_by "name"); one that differs in total
//                  thickness, function, structural layer or layer stack is TYPE_DIMENSION_MISMATCH (never edited). A
//                  default floor name held by a type of the other kind (foundation vs floor) moves to the alternate default
//                  "Plinth <t>mm Foundation Slab" / "Plinth <t>mm Floor Slab"; an explicit name held by a non-candidate is
//                  NAME_IN_USE.
//   2. by spec   - any candidate that matches (matched_by "spec"): "Plinth ..." names first, then by name; the others are
//                  listed in other_matches.
//   3. copy      - the base type: base_type_name when given (must be a candidate with a readable structure), else
//                  BaseChoiceRule; the NEW type gets the requested name and a fresh compound structure (the base type is
//                  never touched).
// A type matches when its total thickness is within 0.5 mm, walls: its Function equals the request's (interior/exterior),
// floors: it has a structural layer when structural is asked, and, when layers are given, its stack has the same layers in
// the same order (function, thickness within 0.5 mm, and material by name when the layer names one).
//
// BaseChoiceRule (no base_type_name): the pool is the candidates with a readable structure and (walls) the asked function,
// (floors with structural) a structural layer; when that pool is empty, every readable candidate (said in the reason).
// The type whose total thickness is closest wins. A tie (within 0.5 mm) is broken by name ONLY when the tied types would
// give the new type the same inherited materials (the core material when layers are omitted; for each given layer without
// a material, the base's material for that layer); otherwise it is TYPE_CHOICE_NEEDED with the candidates: never a silent
// pick. When every given layer names its material, nothing is inherited and a tie goes by name.
//
// LayerPlan: the new type's layers. Without layers: one structure layer of the whole thickness with the base's core
// material (the first structure layer's material, else the first non-membrane layer's). With layers: as given; a layer
// without a material takes the base's material of the first layer with the same function (structure: the core material),
// else none (<By Category>). Materials named by the caller must exist (the Revit side says MATERIAL_NOT_FOUND with options);
// materials are never created.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Core.Families
{
    public enum HostKind
    {
        Wall,
        Floor
    }

    /// <summary>One layer of a compound structure, outside (walls) or top (floors) first.</summary>
    public sealed class LayerSpec
    {
        /// <summary>structure, substrate, insulation, finish1, finish2, membrane (facts may also say structural_deck or none).</summary>
        public string Function { get; set; }

        public double ThicknessMm { get; set; }

        /// <summary>The material's name; null for none (&lt;By Category&gt;) or, in a request, "not given".</summary>
        public string Material { get; set; }

        /// <summary>Facts and plans: the material's element id; null for none or (plans) a name the caller gave, still to resolve.</summary>
        public long? MaterialId { get; set; }

        /// <summary>Plans only: the material came from the base type, not from the caller.</summary>
        public bool Inherited { get; set; }

        public JObject ToJson() => new JObject
        {
            ["function"] = Function,
            ["thickness_mm"] = Math.Round(ThicknessMm, 2),
            ["material"] = Material
        };
    }

    /// <summary>One candidate type as the decisions see it.</summary>
    public sealed class HostTypeFacts
    {
        public string Name { get; set; }

        /// <summary>Its compound structure's total thickness in mm; null when it cannot be read.</summary>
        public double? TotalMm { get; set; }

        /// <summary>Walls: interior, exterior, foundation, retaining, soffit, core_shaft or null. Floors: unused.</summary>
        public string Function { get; set; }

        /// <summary>Its layers, outside/top first; null when the structure cannot be read (never matched, never a base).</summary>
        public IList<LayerSpec> Layers { get; set; }

        /// <summary>It has a structural layer (CompoundStructure.StructuralMaterialIndex set).</summary>
        public bool Structural { get; set; }

        public JObject ToJson(HostKind kind)
        {
            var json = new JObject
            {
                ["type_name"] = Name,
                ["thickness_mm"] = TotalMm.HasValue ? (JToken)Math.Round(TotalMm.Value, 1) : null
            };
            if (kind == HostKind.Wall) json["function"] = Function;
            else json["structural"] = Structural;
            json["core_material"] = HostTypeRule.CoreMaterial(Layers)?.Material;
            json["layers"] = Layers == null ? null : new JArray(Layers.Select(l => l.ToJson()));
            return json;
        }
    }

    /// <summary>The type a call asks for.</summary>
    public sealed class HostTypeRequest
    {
        public HostKind Kind { get; set; }
        public double ThicknessMm { get; set; }

        /// <summary>Walls: interior or exterior.</summary>
        public string Function { get; set; }

        /// <summary>The caller's name; null for the default.</summary>
        public string ExplicitName { get; set; }

        /// <summary>The caller's layers; null when omitted.</summary>
        public IList<LayerSpec> Layers { get; set; }

        public string BaseTypeName { get; set; }

        /// <summary>Floors: a structural layer is asked.</summary>
        public bool Structural { get; set; }

        /// <summary>Floors: a foundation slab type is asked.</summary>
        public bool Foundation { get; set; }

        public string DefaultName => Kind == HostKind.Wall
            ? HostTypeRule.WallDefaultName(ThicknessMm, Function)
            : HostTypeRule.FloorDefaultName(ThicknessMm);

        /// <summary>The default name to use when DefaultName is held by a type of the other kind (floors only).</summary>
        public string AlternateDefaultName => Kind == HostKind.Wall
            ? null
            : "Plinth " + TypeMatchRule.Number(ThicknessMm) + "mm " + (Foundation ? "Foundation Slab" : "Floor Slab");

        public string Noun => Kind == HostKind.Wall ? "Basic Wall" : Foundation ? "foundation slab" : "floor";
    }

    public enum HostTypeStep
    {
        UseExisting,
        CreateNew,
        Refuse
    }

    public sealed class HostTypeDecision
    {
        public HostTypeStep Step { get; internal set; }

        /// <summary>UseExisting: the type to use; Refuse: the type that blocks the request (or -1).</summary>
        public int Index { get; internal set; } = -1;

        /// <summary>The existing type's name, or the new type's name.</summary>
        public string TypeName { get; internal set; }

        /// <summary>UseExisting: "name" or "spec".</summary>
        public string MatchedBy { get; internal set; }

        /// <summary>UseExisting by spec: the other matching types' names.</summary>
        public List<string> OtherMatches { get; } = new List<string>();

        /// <summary>CreateNew: the base type's index.</summary>
        public int BaseIndex { get; internal set; } = -1;

        public string BaseReason { get; internal set; }

        /// <summary>CreateNew: the other candidates looked at, closest first.</summary>
        public JArray Alternatives { get; internal set; } = new JArray();

        /// <summary>CreateNew: the new type's layers (materials to resolve on the Revit side).</summary>
        public List<LayerSpec> Layers { get; internal set; }

        /// <summary>"CODE: message" when Step is Refuse.</summary>
        public string Error { get; internal set; }

        public JObject Data { get; internal set; }

        public string Code => Error == null ? null : Error.Substring(0, Math.Max(0, Error.IndexOf(':')));

        internal static HostTypeDecision Refuse(string error, JObject data = null, int index = -1) =>
            new HostTypeDecision { Step = HostTypeStep.Refuse, Error = error, Data = data ?? new JObject(), Index = index };
    }

    public static class HostTypeRule
    {
        public const double ToleranceMm = 0.5;

        /// <summary>A given stack must add up to thickness_mm within this.</summary>
        public const double SumToleranceMm = 0.01;

        public const double MaxThicknessMm = 5000;
        public const int MaxLayers = 30;
        public const int MaxNameLength = 200;
        public const int MaxListed = 10;

        public static readonly string[] LayerFunctions = { "structure", "finish1", "finish2", "substrate", "insulation", "membrane" };
        public static readonly string[] WallFunctions = { "exterior", "interior" };

        public static string WallDefaultName(double thicknessMm, string function) =>
            "Plinth " + TypeMatchRule.Number(thicknessMm) + "mm " + (string.Equals(function, "exterior", StringComparison.OrdinalIgnoreCase) ? "Exterior" : "Interior");

        public static string FloorDefaultName(double thicknessMm) => "Plinth " + TypeMatchRule.Number(thicknessMm) + "mm Slab";

        // ---- input ----------------------------------------------------------------------------------

        /// <summary>thickness_mm: a number above 0 and at most MaxThicknessMm.</summary>
        public static bool TryThickness(JToken token, out double mm, out string error)
        {
            mm = token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) ? token.Value<double>() : double.NaN;
            error = null;
            if (!double.IsNaN(mm) && !double.IsInfinity(mm) && mm > 0 && mm <= MaxThicknessMm) return true;
            error = "INVALID_PARAMS: thickness_mm is required and must be a number of mm above 0 and at most "
                + MaxThicknessMm.ToString(CultureInfo.InvariantCulture) + ".";
            return false;
        }

        /// <summary>A type name: trimmed, not empty, without the characters Revit refuses.</summary>
        public static bool TryName(JToken token, string field, out string name, out string error)
        {
            name = null;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            name = token.Type == JTokenType.String ? (token.Value<string>() ?? "").Trim() : "";
            var bad = name.IndexOfAny(TypeMatchRule.InvalidNameChars.ToCharArray());
            if (name.Length > 0 && name.Length <= MaxNameLength && bad < 0) return true;
            error = "INVALID_PARAMS: " + field + " must be a non-empty name of at most " + MaxNameLength + " characters"
                + (bad >= 0 ? " without '" + name[bad] + "' (Revit refuses " + TypeMatchRule.InvalidNameChars + " in type names)" : "")
                + " (omit it for the default).";
            name = null;
            return false;
        }

        /// <summary>
        /// layers: [{function, thickness_mm, material?}], outside/top first. Each function from LayerFunctions; a membrane has
        /// thickness 0 (Revit's rule), every other layer more than 0; at least one structure layer; the sum equals
        /// thickness_mm. Returns null layers (no error) when the token is absent.
        /// </summary>
        public static bool TryLayers(JToken token, double thicknessMm, out List<LayerSpec> layers, out string error)
        {
            layers = null;
            error = null;
            if (token == null || token.Type == JTokenType.Null) return true;
            var array = token as JArray;
            const string shape = " layers is a list of {function, thickness_mm, material?}, outside (walls) or top (floors) first.";
            if (array == null || array.Count == 0 || array.Count > MaxLayers)
            {
                error = "INVALID_PARAMS: layers must hold 1 to " + MaxLayers + " layers (omit it for one structure layer)." + shape;
                return false;
            }
            var list = new List<LayerSpec>();
            for (var i = 0; i < array.Count; i++)
            {
                var label = "layers[" + i + "]";
                if (!(array[i] is JObject layer))
                {
                    error = "INVALID_PARAMS: " + label + " must be an object." + shape;
                    return false;
                }
                var unknown = layer.Properties().Select(p => p.Name).FirstOrDefault(n => n != "function" && n != "thickness_mm" && n != "material");
                if (unknown != null)
                {
                    error = "INVALID_PARAMS: " + label + " has unknown field '" + unknown + "'." + shape;
                    return false;
                }
                var function = layer["function"]?.Type == JTokenType.String ? layer.Value<string>("function").Trim().ToLowerInvariant() : null;
                if (function == null || !LayerFunctions.Contains(function))
                {
                    error = "INVALID_PARAMS: " + label + ".function must be one of " + string.Join(", ", LayerFunctions) + ".";
                    return false;
                }
                var t = layer["thickness_mm"];
                var mm = t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? t.Value<double>() : double.NaN;
                if (double.IsNaN(mm) || double.IsInfinity(mm) || mm < 0 || mm > MaxThicknessMm)
                {
                    error = "INVALID_PARAMS: " + label + ".thickness_mm must be a number of mm from 0 to " + MaxThicknessMm.ToString(CultureInfo.InvariantCulture) + ".";
                    return false;
                }
                if (function == "membrane" && mm != 0)
                {
                    error = "INVALID_PARAMS: " + label + " is a membrane, which Revit requires to be 0 mm thick; give it thickness_mm 0 "
                        + "(or another function, such as substrate).";
                    return false;
                }
                if (function != "membrane" && mm == 0)
                {
                    error = "INVALID_PARAMS: " + label + ".thickness_mm must be above 0 (only a membrane is 0 mm thick).";
                    return false;
                }
                string material = null;
                var m = layer["material"];
                if (m != null && m.Type != JTokenType.Null)
                {
                    material = m.Type == JTokenType.String ? (m.Value<string>() ?? "").Trim() : "";
                    if (material.Length == 0)
                    {
                        error = "INVALID_PARAMS: " + label + ".material must be a material name (omit it to keep the base type's material).";
                        return false;
                    }
                }
                list.Add(new LayerSpec { Function = function, ThicknessMm = mm, Material = material });
            }
            if (list.All(l => l.Function != "structure"))
            {
                error = "INVALID_PARAMS: layers needs at least one structure layer (the core Revit builds around).";
                return false;
            }
            var sum = list.Sum(l => l.ThicknessMm);
            if (Math.Abs(sum - thicknessMm) > SumToleranceMm)
            {
                error = "INVALID_PARAMS: the layers add up to " + TypeMatchRule.Number(sum) + " mm, not thickness_mm " + TypeMatchRule.Number(thicknessMm)
                    + " mm; they must be equal.";
                return false;
            }
            layers = list;
            return true;
        }

        // ---- matching -------------------------------------------------------------------------------

        /// <summary>The layer whose material is the type's core material: the first structure layer, else the first non-membrane layer.</summary>
        public static LayerSpec CoreMaterial(IList<LayerSpec> layers)
        {
            if (layers == null) return null;
            return layers.FirstOrDefault(l => l.Function == "structure") ?? layers.FirstOrDefault(l => l.Function != "membrane");
        }

        /// <summary>How type differs from the request, in words; empty when it matches.</summary>
        public static List<string> Differences(HostTypeFacts type, HostTypeRequest request)
        {
            var differences = new List<string>();
            if (type.Layers == null || !type.TotalMm.HasValue)
            {
                differences.Add("its layer structure cannot be read");
                return differences;
            }
            if (Math.Abs(type.TotalMm.Value - request.ThicknessMm) > ToleranceMm)
                differences.Add("it is " + TypeMatchRule.Number(Math.Round(type.TotalMm.Value, 1)) + " mm thick, not " + TypeMatchRule.Number(request.ThicknessMm) + " mm");
            if (request.Kind == HostKind.Wall && !string.Equals(type.Function, request.Function, StringComparison.OrdinalIgnoreCase))
                differences.Add("its function is " + (type.Function ?? "unknown") + ", not " + request.Function);
            if (request.Kind == HostKind.Floor && request.Structural && !type.Structural)
                differences.Add("it has no structural layer");
            if (request.Layers != null)
            {
                var stack = StackDifference(type.Layers, request.Layers);
                if (stack != null) differences.Add(stack);
            }
            return differences;
        }

        public static bool Matches(HostTypeFacts type, HostTypeRequest request) => Differences(type, request).Count == 0;

        /// <summary>The first way the type's layers differ from the asked ones, or null when they are the same.</summary>
        public static string StackDifference(IList<LayerSpec> actual, IList<LayerSpec> asked)
        {
            if (actual.Count != asked.Count)
                return "it has " + actual.Count + " layer" + (actual.Count == 1 ? "" : "s") + " (" + Describe(actual) + "), not " + asked.Count + " (" + Describe(asked) + ")";
            for (var i = 0; i < asked.Count; i++)
            {
                var a = actual[i];
                var b = asked[i];
                if (a.Function != b.Function || Math.Abs(a.ThicknessMm - b.ThicknessMm) > ToleranceMm)
                    return "its layer " + i + " is " + Describe(a) + ", not " + Describe(b);
                if (b.Material != null && !string.Equals(a.Material, b.Material, StringComparison.OrdinalIgnoreCase))
                    return "its layer " + i + " is of " + (a.Material == null ? "no material" : "'" + a.Material + "'") + ", not '" + b.Material + "'";
            }
            return null;
        }

        public static string Describe(IList<LayerSpec> layers) => string.Join(" + ", layers.Select(Describe));

        public static string Describe(LayerSpec layer) =>
            layer.Function + " " + TypeMatchRule.Number(Math.Round(layer.ThicknessMm, 1)) + " mm" + (layer.Material == null ? "" : " " + layer.Material);

        private static int IndexByName(IList<HostTypeFacts> types, string name)
        {
            for (var i = 0; i < types.Count; i++)
                if (types[i].Name == name) return i;
            for (var i = 0; i < types.Count; i++)
                if (string.Equals(types[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static bool Holds(IEnumerable<string> names, string name) =>
            names != null && names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        // ---- the decision ---------------------------------------------------------------------------

        /// <summary>
        /// candidates: the types the request may use or copy (see the file header). otherNames: names of the class's other
        /// types (curtain/stacked walls; floor types of the other foundation kind), which only block a new name.
        /// </summary>
        public static HostTypeDecision Decide(IList<HostTypeFacts> candidates, IList<string> otherNames, HostTypeRequest request)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (request == null) throw new ArgumentNullException(nameof(request));

            // 1. by name.
            var names = request.ExplicitName != null
                ? new[] { request.ExplicitName }
                : new[] { request.DefaultName, request.AlternateDefaultName }.Where(n => n != null).ToArray();
            string newName = null;
            foreach (var name in names)
            {
                var index = IndexByName(candidates, name);
                if (index >= 0)
                {
                    var differences = Differences(candidates[index], request);
                    if (differences.Count == 0)
                        return new HostTypeDecision { Step = HostTypeStep.UseExisting, Index = index, TypeName = candidates[index].Name, MatchedBy = "name" };
                    return Mismatch(candidates[index], index, request, differences);
                }
                if (!Holds(otherNames, name))
                {
                    newName = name;
                    break;
                }
            }

            // 2. by spec.
            var matches = Enumerable.Range(0, candidates.Count).Where(i => Matches(candidates[i], request))
                .OrderBy(i => (candidates[i].Name ?? "").StartsWith("Plinth ", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(i => candidates[i].Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (matches.Count > 0)
            {
                var found = new HostTypeDecision { Step = HostTypeStep.UseExisting, Index = matches[0], TypeName = candidates[matches[0]].Name, MatchedBy = "spec" };
                found.OtherMatches.AddRange(matches.Skip(1).Select(i => candidates[i].Name));
                return found;
            }

            if (newName == null)
            {
                var taken = names[names.Length - 1];
                return HostTypeDecision.Refuse("NAME_IN_USE: '" + taken + "' is already the name of another " + (request.Kind == HostKind.Wall
                        ? "wall type (curtain or stacked wall)" : "floor type (" + (request.Foundation ? "not a foundation slab" : "a foundation slab") + ")")
                    + ", so a new " + request.Noun + " type cannot take it; existing types are never renamed or edited. Pass another name.",
                    new JObject { ["type_name"] = taken });
            }

            // 3. copy a base type.
            BaseChoice choice;
            if (request.BaseTypeName != null)
            {
                var index = IndexByName(candidates, request.BaseTypeName);
                if (index < 0) return BaseNotFound(candidates, otherNames, request);
                if (candidates[index].Layers == null || !candidates[index].TotalMm.HasValue)
                    return HostTypeDecision.Refuse("BASE_TYPE_UNREADABLE: the layer structure of '" + candidates[index].Name + "' cannot be read, so it "
                        + "cannot be the base type; pass another base_type_name (or omit it).", Candidates(candidates, request), index);
                choice = new BaseChoice { Index = index, Reason = "base_type_name given by the caller" };
                choice.Alternatives.AddRange(Ranked(candidates, request).Where(i => i != index));
            }
            else
            {
                choice = ChooseBase(candidates, request);
                if (choice.Error != null) return HostTypeDecision.Refuse(choice.Error, choice.Data);
            }
            var decision = new HostTypeDecision
            {
                Step = HostTypeStep.CreateNew,
                TypeName = newName,
                BaseIndex = choice.Index,
                BaseReason = choice.Reason,
                Alternatives = new JArray(choice.Alternatives.Take(MaxListed).Select(i => candidates[i].ToJson(request.Kind))),
                Layers = PlanLayers(request, candidates[choice.Index])
            };
            return decision;
        }

        private static HostTypeDecision Mismatch(HostTypeFacts type, int index, HostTypeRequest request, List<string> differences)
        {
            var advice = request.ExplicitName != null
                ? "pass another name (or omit name: an existing " + request.Noun + " type with this spec is then used, else a new one is made)"
                : "pass name to give the new type another name";
            var data = type.ToJson(request.Kind);
            data["differences"] = new JArray(differences);
            data["requested"] = RequestJson(request);
            return HostTypeDecision.Refuse("TYPE_DIMENSION_MISMATCH: " + request.Noun + " type '" + type.Name + "' exists but " + string.Join("; ", differences)
                + ". Existing types are never edited; " + advice + ". Nothing was changed.", data, index);
        }

        private static HostTypeDecision BaseNotFound(IList<HostTypeFacts> candidates, IList<string> otherNames, HostTypeRequest request)
        {
            var other = Holds(otherNames, request.BaseTypeName);
            var why = other
                ? "'" + request.BaseTypeName + "' is not a " + request.Noun + " type (" + (request.Kind == HostKind.Wall
                    ? "a curtain or stacked wall cannot be the base of a Basic Wall type"
                    : request.Foundation ? "a floor type cannot become a foundation slab type" : "a foundation slab type cannot become a floor type") + ")"
                : "no " + request.Noun + " type is named '" + request.BaseTypeName + "'";
            return HostTypeDecision.Refuse("BASE_TYPE_NOT_FOUND: " + why + ". Pass one of the listed types as base_type_name, or omit it to let Plinth "
                + "choose. Nothing was changed.", Candidates(candidates, request));
        }

        private static JObject Candidates(IList<HostTypeFacts> candidates, HostTypeRequest request) => new JObject
        {
            ["candidates"] = new JArray(Ranked(candidates, request).Take(MaxListed * 3).Select(i => candidates[i].ToJson(request.Kind)))
        };

        private static JObject RequestJson(HostTypeRequest request)
        {
            var json = new JObject { ["thickness_mm"] = request.ThicknessMm };
            if (request.Kind == HostKind.Wall) json["function"] = request.Function;
            else
            {
                json["structural"] = request.Structural;
                json["foundation"] = request.Foundation;
            }
            json["layers"] = request.Layers == null ? null : new JArray(request.Layers.Select(l => l.ToJson()));
            return json;
        }

        // ---- base choice ----------------------------------------------------------------------------

        public sealed class BaseChoice
        {
            public int Index { get; internal set; } = -1;
            public string Reason { get; internal set; }

            /// <summary>The other candidates, closest first.</summary>
            public List<int> Alternatives { get; } = new List<int>();

            public string Error { get; internal set; }
            public JObject Data { get; internal set; }
        }

        /// <summary>Readable candidates, closest in total thickness first, then by name.</summary>
        private static List<int> Ranked(IList<HostTypeFacts> candidates, HostTypeRequest request) =>
            Enumerable.Range(0, candidates.Count).Where(i => candidates[i].TotalMm.HasValue && candidates[i].Layers != null)
                .OrderBy(i => Math.Abs(candidates[i].TotalMm.Value - request.ThicknessMm))
                .ThenBy(i => candidates[i].Name, StringComparer.OrdinalIgnoreCase).ToList();

        public static BaseChoice ChooseBase(IList<HostTypeFacts> candidates, HostTypeRequest request)
        {
            var choice = new BaseChoice();
            var readable = Ranked(candidates, request);
            if (readable.Count == 0)
            {
                choice.Error = "BASE_TYPE_NOT_FOUND: the project has no " + request.Noun + " type " + (candidates.Count == 0 ? "" : "with a readable layer structure ")
                    + "to copy" + (request.Foundation ? " (a foundation slab type can only be copied from another one; load one, e.g. from a structural "
                        + "template, or transfer it from another project)" : "") + ". Nothing was changed.";
                choice.Data = Candidates(candidates, request);
                return choice;
            }
            List<int> pool;
            string poolNote;
            if (request.Kind == HostKind.Wall)
            {
                pool = readable.Where(i => string.Equals(candidates[i].Function, request.Function, StringComparison.OrdinalIgnoreCase)).ToList();
                poolNote = pool.Count > 0 ? request.Function + " Basic Wall types" : null;
                if (pool.Count == 0) poolNote = "no Basic Wall type has function " + request.Function + ", so all Basic Wall types";
            }
            else if (request.Structural)
            {
                pool = readable.Where(i => candidates[i].Structural).ToList();
                poolNote = pool.Count > 0 ? request.Noun + " types with a structural layer" : "no " + request.Noun + " type has a structural layer, so all " + request.Noun + " types";
            }
            else
            {
                pool = readable;
                poolNote = request.Noun + " types";
            }
            if (pool.Count == 0) pool = readable;

            var best = Math.Abs(candidates[pool[0]].TotalMm.Value - request.ThicknessMm);
            var tied = pool.Where(i => Math.Abs(candidates[i].TotalMm.Value - request.ThicknessMm) <= best + ToleranceMm).ToList();
            var chosen = tied[0];
            string tieNote = "";
            if (tied.Count > 1)
            {
                var keys = tied.Select(i => InheritedKey(request, candidates[i])).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (keys.Count > 1)
                {
                    choice.Error = "TYPE_CHOICE_NEEDED: " + tied.Count + " " + request.Noun + " types are equally close to " + TypeMatchRule.Number(request.ThicknessMm)
                        + " mm (" + string.Join(", ", tied.Select(i => "'" + candidates[i].Name + "' " + TypeMatchRule.Number(Math.Round(candidates[i].TotalMm.Value, 1)) + " mm"))
                        + ") and would give the new type different materials. Ask the user which to copy and pass base_type_name (or give every layer "
                        + "its material). Nothing was changed.";
                    choice.Data = new JObject
                    {
                        ["candidates"] = new JArray(tied.Select(i => candidates[i].ToJson(request.Kind))),
                        ["others"] = new JArray(pool.Except(tied).Take(MaxListed).Select(i => candidates[i].ToJson(request.Kind)))
                    };
                    return choice;
                }
                tieNote = "; tied with " + string.Join(", ", tied.Skip(1).Select(i => "'" + candidates[i].Name + "'"))
                    + ", which would give the same materials, so the first by name";
            }
            choice.Index = chosen;
            var type = candidates[chosen];
            var distance = Math.Abs(type.TotalMm.Value - request.ThicknessMm);
            choice.Reason = "'" + type.Name + "' (" + TypeMatchRule.Number(Math.Round(type.TotalMm.Value, 1)) + " mm"
                + (CoreMaterial(type.Layers)?.Material == null ? "" : ", core " + CoreMaterial(type.Layers).Material) + ") is the closest in thickness"
                + (distance <= ToleranceMm ? "" : " (" + TypeMatchRule.Number(Math.Round(distance, 1)) + " mm off)") + " among the " + poolNote + tieNote + ".";
            choice.Alternatives.AddRange(readable.Where(i => i != chosen).OrderBy(i => pool.Contains(i) ? 0 : 1).ThenBy(i => readable.IndexOf(i)));
            return choice;
        }

        /// <summary>What the new type would inherit from this base: the materials of the layers the caller left without one.</summary>
        private static string InheritedKey(HostTypeRequest request, HostTypeFacts baseType) =>
            string.Join("|", PlanLayers(request, baseType).Where(l => l.Inherited).Select(l => l.Material ?? "<none>"));

        // ---- the new type's layers ------------------------------------------------------------------

        /// <summary>The new type's layers (see the file header); a caller-named material has MaterialId null and Inherited false.</summary>
        public static List<LayerSpec> PlanLayers(HostTypeRequest request, HostTypeFacts baseType)
        {
            var core = CoreMaterial(baseType?.Layers);
            if (request.Layers == null)
                return new List<LayerSpec>
                {
                    new LayerSpec { Function = "structure", ThicknessMm = request.ThicknessMm, Material = core?.Material, MaterialId = core?.MaterialId, Inherited = true }
                };
            return request.Layers.Select(l =>
            {
                if (l.Material != null) return new LayerSpec { Function = l.Function, ThicknessMm = l.ThicknessMm, Material = l.Material };
                var from = l.Function == "structure" ? core : baseType?.Layers?.FirstOrDefault(b => b.Function == l.Function);
                return new LayerSpec { Function = l.Function, ThicknessMm = l.ThicknessMm, Material = from?.Material, MaterialId = from?.MaterialId, Inherited = true };
            }).ToList();
        }

        /// <summary>Core boundaries for a stack: shell layers before the first and after the last structure layer.</summary>
        public static void ShellCounts(IList<LayerSpec> layers, out int exterior, out int interior)
        {
            var first = -1;
            var last = -1;
            for (var i = 0; i < layers.Count; i++)
            {
                if (layers[i].Function != "structure") continue;
                if (first < 0) first = i;
                last = i;
            }
            if (first < 0)
            {
                exterior = 0;
                interior = 0;
                return;
            }
            exterior = first;
            interior = layers.Count - 1 - last;
        }
    }
}
