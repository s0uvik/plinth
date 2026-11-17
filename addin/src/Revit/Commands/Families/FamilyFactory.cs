// New in Plinth: rvt-mcp cannot author families. FamilyFactory builds a parametric rectangular concrete family from
// Revit's own family template, for ensure_family_type when no family of the asked name or a related one exists anywhere
// and the caller passed allow_create: true (the user agreed). A recipe also names the related families FamilyAdapter
// may adapt and the words find_family_files' category filter looks for.
// Each recipe names a category and a template and draws one extrusion whose faces are aligned and locked to
// reference planes or levels, with the section driven by type parameters b and h through labelled dimensions:
//   structural_column: sketched on the Lower Ref. Level (so the base follows it), top face locked to the Upper Ref.
//     Level, sides locked to Left/Right/Front/Back planes held symmetric by EQ dimensions about the Center planes,
//     b labelling Left-Right and h labelling Front-Back.
//   structural_framing: sketched on the Member Left plane (so the start follows it) and extruded to the Member Right
//     plane with the end face locked there, so the beam flexes with its length; top face locked to the Ref. Level
//     (top of beam at the level), bottom face locked to a Beam Bottom plane h below it, sides locked to Beam Front /
//     Beam Back planes held symmetric about Center (Front/Back) with b labelling them.
// After building, a flex check in a transaction that is always rolled back changes b and h and moves the stretch
// datum (Upper Ref. Level, Member Right) and checks the solid follows, so a family that would not flex is never saved.
// Every failure names the step that failed; the caller closes the family document whatever happens.
// A box (BoxRecipe, for create_box_family, 2026-11-17: a fit-out item no family suits, created only when the user says
// so) is one extrusion W x D x H with type parameters Width, Depth, Height and Material, its origin at the middle of its
// back edge (back on +Y, front facing -Y) and its base at the level; its flex check keeps the back on the origin.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Plinth.Revit.Bridge;
using Plinth.Revit.Core.Logging;
using Newtonsoft.Json.Linq;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace Plinth.Revit.Commands.Families
{
    /// <summary>A failure while building a family document, naming the step that failed.</summary>
    internal sealed class FamilyBuildException : Exception
    {
        public FamilyBuildException(string step, string message) : base(message)
        {
            Step = step;
        }

        public string Step { get; }
    }

    /// <summary>A section size given on the wire in mm: its key, the family parameter it drives and the alternative
    /// name accepted on families that already exist (Width for b, Depth for h). Door and window sizes instead name
    /// built-in parameters looked up first (DOOR_WIDTH, FAMILY_WIDTH_PARAM...) and then several parameter names; their
    /// Parameter is only the label used in results.</summary>
    internal sealed class SectionDimension
    {
        private readonly string[] _names;

        public SectionDimension(string key, string parameter, string alternative)
        {
            Key = key;
            Parameter = parameter;
            Alternative = alternative;
            _names = new[] { parameter, alternative };
            BuiltIns = new BuiltInParameter[0];
        }

        public SectionDimension(string key, string label, string[] names, BuiltInParameter[] builtIns)
        {
            Key = key;
            Parameter = label;
            Alternative = names.Length > 1 ? names[1] : null;
            _names = names;
            BuiltIns = builtIns;
        }

        public string Key { get; }
        public string Parameter { get; }
        public string Alternative { get; }
        public string[] Names => _names;

        /// <summary>Built-in parameters tried, in order, before the names (none for structural sections).</summary>
        public BuiltInParameter[] BuiltIns { get; }
    }

    /// <summary>What a recipe built, for the flex check.</summary>
    internal sealed class BuiltGeometry
    {
        public Extrusion Extrusion;
        public XYZ WidthAxis;
        public double WidthCenter;
        public XYZ DepthAxis;

        /// <summary>Set when h is centred on a plane (column); otherwise DepthTop is the fixed top (beam).</summary>
        public double? DepthCenter;

        public double DepthTop;

        /// <summary>The datum the element stretches to (Upper Ref. Level, Member Right), its axis and the fixed other end.</summary>
        public Element StretchDatum;

        public XYZ StretchAxis;
        public double StretchFixed;
    }

    /// <summary>One way to make a family of a category. Add a recipe here to support another category.</summary>
    internal abstract class FamilyRecipe
    {
        public static readonly SectionDimension B = new SectionDimension("b_mm", "b", "Width");
        public static readonly SectionDimension H = new SectionDimension("h_mm", "h", "Depth");

        public static readonly FamilyRecipe[] All = { new StructuralColumnRecipe(), new StructuralFramingRecipe() };

        public static FamilyRecipe Find(string key) => All.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));

        /// <summary>Every category ensure_family_type takes: the buildable ones plus door and window (find, adapt a type, ask; never built).</summary>
        public static readonly FamilyRecipe[] Ensurable = All.Concat(new FamilyRecipe[] { new DoorRecipe(), new WindowRecipe() }).ToArray();

        public static FamilyRecipe FindEnsurable(string key) => Ensurable.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));

        public abstract string Key { get; }
        public abstract BuiltInCategory Category { get; }
        public abstract string TemplateFileName { get; }
        public virtual IReadOnlyList<SectionDimension> Dimensions => new[] { B, H };

        /// <summary>Door or window: no template build, no adapt; types are matched by size and Type Mark. Null for structural.</summary>
        public virtual Core.Families.OpeningKind? Opening => null;

        /// <summary>
        /// A related family worth adapting has a name containing one word of every group (case-insensitive, any separators),
        /// e.g. M_Concrete-Rectangular Beam or Concrete-Rectangular-Column.
        /// </summary>
        public abstract string[][] RelatedNameWords { get; }

        /// <summary>Words that suggest this category in a file name or its folder (find_family_files' quick filter).</summary>
        public abstract string[] CategoryWords { get; }

        public bool IsRelatedName(string name) =>
            RelatedNameWords.All(group => group.Any(word => name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0));

        public bool LooksLikeCategory(string fileName, string folder) =>
            CategoryWords.Any(word => fileName.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0
                || (folder ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>Inside the open family transaction, with b and h set on the only type: planes, extrusion, locks, labels.</summary>
        internal abstract BuiltGeometry Build(FamilyBuilder fb, FamilyParameter pb, FamilyParameter ph, double b, double h);
    }

    internal sealed class StructuralColumnRecipe : FamilyRecipe
    {
        public override string Key => "structural_column";
        public override BuiltInCategory Category => BuiltInCategory.OST_StructuralColumns;
        public override string TemplateFileName => "Metric Structural Column.rft";
        public override string[][] RelatedNameWords => new[] { new[] { "concrete" }, new[] { "rectangular", "rect" }, new[] { "column" } };
        public override string[] CategoryWords => new[] { "column" };

        internal override BuiltGeometry Build(FamilyBuilder fb, FamilyParameter pb, FamilyParameter ph, double b, double h)
        {
            var doc = fb.Doc;
            var lower = FamilyBuilder.Step("find the Lower Ref. Level", () => fb.FindLevel("Lower Ref. Level", false));
            var upper = FamilyBuilder.Step("find the Upper Ref. Level", () => fb.FindLevel("Upper Ref. Level", false));
            var plan = FamilyBuilder.Step("find the Lower Ref. Level plan view", () => fb.FindPlanView(lower));
            var front = FamilyBuilder.Step("find the Front elevation view", () => fb.FindElevation("Front", XYZ.BasisY));
            var z0 = lower.ProjectElevation;
            var z1 = upper.ProjectElevation;
            if (z1 - z0 <= FamilyBuilder.Tolerance)
                throw new FamilyBuildException("check the template levels", "the Upper Ref. Level is not above the Lower Ref. Level");

            var centerX = FamilyBuilder.Step("find the Center (Left/Right) reference plane", () => fb.CenterPlane("Center (Left/Right)", XYZ.BasisX));
            var centerY = FamilyBuilder.Step("find the Center (Front/Back) reference plane", () => fb.CenterPlane("Center (Front/Back)", XYZ.BasisY));
            var cx = FamilyBuilder.Coordinate(centerX, XYZ.BasisX);
            var cy = FamilyBuilder.Coordinate(centerY, XYZ.BasisY);
            var basePoint = new XYZ(cx, cy, z0);
            var halfLength = Math.Max(b, h) / 2 + FamilyBuilder.Margin;

            // b across X: Left/Right planes run along Y; their dimensions sit in front of the column.
            var x = fb.SymmetricPair("b", plan, centerX, XYZ.BasisX, "Left", "Right", b, pb, XYZ.BasisY, XYZ.BasisZ, basePoint, halfLength,
                -(h / 2 + FamilyBuilder.Margin));
            // h across Y: Front/Back planes run along X; their dimensions sit left of the column.
            var y = fb.SymmetricPair("h", plan, centerY, XYZ.BasisY, "Front", "Back", h, ph, XYZ.BasisX, XYZ.BasisZ, basePoint, halfLength,
                -(b / 2 + FamilyBuilder.Margin));

            var extrusion = FamilyBuilder.Step("create the column extrusion on the Lower Ref. Level", () =>
            {
                var sketch = SketchPlane.Create(doc, lower.Id);
                var profile = FamilyBuilder.Rectangle(
                    new XYZ(cx - b / 2, cy - h / 2, z0), new XYZ(cx + b / 2, cy - h / 2, z0),
                    new XYZ(cx + b / 2, cy + h / 2, z0), new XYZ(cx - b / 2, cy + h / 2, z0));
                return doc.FamilyCreate.NewExtrusion(true, profile, sketch, z1 - z0);
            });
            doc.Regenerate();

            FamilyBuilder.Step("lock the left face to the Left plane", () => fb.Align(plan, x[0].GetReference(), extrusion, -XYZ.BasisX));
            FamilyBuilder.Step("lock the right face to the Right plane", () => fb.Align(plan, x[1].GetReference(), extrusion, XYZ.BasisX));
            FamilyBuilder.Step("lock the front face to the Front plane", () => fb.Align(plan, y[0].GetReference(), extrusion, -XYZ.BasisY));
            FamilyBuilder.Step("lock the back face to the Back plane", () => fb.Align(plan, y[1].GetReference(), extrusion, XYZ.BasisY));
            FamilyBuilder.Step("lock the top face to the Upper Ref. Level", () => fb.Align(front, upper.GetPlaneReference(), extrusion, XYZ.BasisZ));

            return new BuiltGeometry
            {
                Extrusion = extrusion,
                WidthAxis = XYZ.BasisX,
                WidthCenter = cx,
                DepthAxis = XYZ.BasisY,
                DepthCenter = cy,
                StretchDatum = upper,
                StretchAxis = XYZ.BasisZ,
                StretchFixed = z0
            };
        }
    }

    internal sealed class StructuralFramingRecipe : FamilyRecipe
    {
        public override string Key => "structural_framing";
        public override BuiltInCategory Category => BuiltInCategory.OST_StructuralFraming;
        public override string TemplateFileName => "Metric Structural Framing - Beams and Braces.rft";
        public override string[][] RelatedNameWords => new[] { new[] { "concrete" }, new[] { "rectangular", "rect" }, new[] { "beam" } };
        public override string[] CategoryWords => new[] { "framing", "beam", "brace", "joist", "girder" };

        internal override BuiltGeometry Build(FamilyBuilder fb, FamilyParameter pb, FamilyParameter ph, double b, double h)
        {
            var doc = fb.Doc;
            var level = FamilyBuilder.Step("find the Ref. Level", () => fb.FindLevel("Ref. Level", true));
            var plan = FamilyBuilder.Step("find the Ref. Level plan view", () => fb.FindPlanView(level));
            var front = FamilyBuilder.Step("find the Front elevation view", () => fb.FindElevation("Front", XYZ.BasisY));
            var memberLeft = FamilyBuilder.Step("find the Member Left reference plane", () => fb.RequiredPlane("Member Left", XYZ.BasisX));
            var memberRight = FamilyBuilder.Step("find the Member Right reference plane", () => fb.RequiredPlane("Member Right", XYZ.BasisX));
            var centerY = FamilyBuilder.Step("find the Center (Front/Back) reference plane", () => fb.CenterPlane("Center (Front/Back)", XYZ.BasisY));

            var xl = FamilyBuilder.Coordinate(memberLeft, XYZ.BasisX);
            var xr = FamilyBuilder.Coordinate(memberRight, XYZ.BasisX);
            if (Math.Abs(xr - xl) <= FamilyBuilder.Tolerance)
                throw new FamilyBuildException("check the member planes", "Member Left and Member Right are at the same place");
            var direction = Math.Sign(xr - xl);
            var cy = FamilyBuilder.Coordinate(centerY, XYZ.BasisY);
            var top = level.ProjectElevation;
            var xMid = (xl + xr) / 2;
            var halfSpan = Math.Abs(xr - xl) / 2 + FamilyBuilder.Margin;

            // b across Y: Beam Front/Back planes run along the beam in plan; dimensions sit beyond the start.
            var y = fb.SymmetricPair("b", plan, centerY, XYZ.BasisY, "Beam Front", "Beam Back", b, pb, XYZ.BasisX, XYZ.BasisZ,
                new XYZ(xMid, cy, top), halfSpan, -halfSpan);

            // h down from the Ref. Level: a Beam Bottom plane, drawn in the Front elevation, labelled from the level.
            var bottom = FamilyBuilder.Step("create the Beam Bottom reference plane", () =>
                fb.EnsurePlane("Beam Bottom", XYZ.BasisZ, top - h, XYZ.BasisX, XYZ.BasisY, new XYZ(xMid, cy, top), halfSpan, front));
            FamilyBuilder.Step("label h between the Ref. Level and Beam Bottom", () =>
                fb.LabelledDimension(front, level.GetPlaneReference(), bottom.GetReference(),
                    Line.CreateBound(new XYZ(xMid - direction * halfSpan, cy, top), new XYZ(xMid - direction * halfSpan, cy, top - h)), ph));
            doc.Regenerate();
            FamilyBuilder.Step("check the Beam Bottom position", () => fb.CheckAt(bottom, XYZ.BasisZ, top - h));

            var extrusion = FamilyBuilder.Step("create the beam extrusion on the Member Left plane", () =>
            {
                var sketch = SketchPlane.Create(doc, memberLeft.Id);
                var normal = sketch.GetPlane().Normal;
                var profile = FamilyBuilder.Rectangle(
                    new XYZ(xl, cy - b / 2, top - h), new XYZ(xl, cy + b / 2, top - h),
                    new XYZ(xl, cy + b / 2, top), new XYZ(xl, cy - b / 2, top));
                // The extrusion runs along the sketch plane's normal; a negative end extrudes the other way.
                return doc.FamilyCreate.NewExtrusion(true, profile, sketch, (xr - xl) * normal.DotProduct(XYZ.BasisX));
            });
            doc.Regenerate();

            FamilyBuilder.Step("lock the end face to the Member Right plane", () =>
                fb.Align(plan, memberRight.GetReference(), extrusion, XYZ.BasisX * direction));
            FamilyBuilder.Step("lock the front face to the Beam Front plane", () => fb.Align(plan, y[0].GetReference(), extrusion, -XYZ.BasisY));
            FamilyBuilder.Step("lock the back face to the Beam Back plane", () => fb.Align(plan, y[1].GetReference(), extrusion, XYZ.BasisY));
            FamilyBuilder.Step("lock the top face to the Ref. Level", () => fb.Align(front, level.GetPlaneReference(), extrusion, XYZ.BasisZ));
            FamilyBuilder.Step("lock the bottom face to the Beam Bottom plane", () => fb.Align(front, bottom.GetReference(), extrusion, -XYZ.BasisZ));

            return new BuiltGeometry
            {
                Extrusion = extrusion,
                WidthAxis = XYZ.BasisY,
                WidthCenter = cy,
                DepthAxis = XYZ.BasisZ,
                DepthTop = top,
                StretchDatum = memberRight,
                StretchAxis = XYZ.BasisX * direction,
                StretchFixed = xl * direction
            };
        }
    }

    /// <summary>What the box recipe built: the extrusion and where its origin is in the family (feet).</summary>
    internal sealed class BoxGeometry
    {
        public Extrusion Extrusion;
        public double Cx, Cy, Z0;
    }

    /// <summary>
    /// A plain box for a fit-out item (create_box_family): one extrusion sketched on the Ref. Level (so its base follows
    /// the level), its sides locked to Box Left / Box Right planes held symmetric about Center (Left/Right) with Width
    /// labelling them, its back face locked to Center (Front/Back) (the origin is the middle of the back edge, the back on
    /// +Y), its front face locked to a Box Front plane Depth in front of it (-Y), its top locked to a Box Top plane Height
    /// above the level. The centre planes are pinned so a changed Depth moves the front, never the origin.
    /// </summary>
    internal static class BoxRecipe
    {
        public static readonly SectionDimension Width = new SectionDimension("width_mm", "Width", null);
        public static readonly SectionDimension Depth = new SectionDimension("depth_mm", "Depth", null);
        public static readonly SectionDimension Height = new SectionDimension("height_mm", "Height", null);

        internal static BoxGeometry Build(FamilyBuilder fb, FamilyParameter pw, FamilyParameter pd, FamilyParameter ph, double w, double d, double h)
        {
            var doc = fb.Doc;
            var level = FamilyBuilder.Step("find the Ref. Level", () => fb.FindLevel("Ref. Level", true));
            var plan = FamilyBuilder.Step("find the Ref. Level plan view", () => fb.FindPlanView(level));
            var front = FamilyBuilder.Step("find the Front elevation view", () => fb.FindElevation("Front", XYZ.BasisY));
            var centerX = FamilyBuilder.Step("find the Center (Left/Right) reference plane", () => fb.CenterPlane("Center (Left/Right)", XYZ.BasisX));
            var centerY = FamilyBuilder.Step("find the Center (Front/Back) reference plane", () => fb.CenterPlane("Center (Front/Back)", XYZ.BasisY));
            FamilyBuilder.Step("pin the centre reference planes", () =>
            {
                if (!centerX.Pinned) centerX.Pinned = true;
                if (!centerY.Pinned) centerY.Pinned = true;
            });
            var cx = FamilyBuilder.Coordinate(centerX, XYZ.BasisX);
            var cy = FamilyBuilder.Coordinate(centerY, XYZ.BasisY);
            var z0 = level.ProjectElevation;
            var basePoint = new XYZ(cx, cy, z0);
            var halfLength = Math.Max(w, d) + FamilyBuilder.Margin;
            var side = cx - w / 2 - FamilyBuilder.Margin; // where the depth and height dimensions sit: left of the box

            // Width across X: Box Left/Right planes run along Y; their dimensions sit behind the back (+Y).
            var x = fb.SymmetricPair("Width", plan, centerX, XYZ.BasisX, "Box Left", "Box Right", w, pw, XYZ.BasisY, XYZ.BasisZ, basePoint,
                halfLength, FamilyBuilder.Margin);

            // Depth: a Box Front plane d in front of the back (the Center (Front/Back) plane), labelled from it.
            var frontPlane = FamilyBuilder.Step("create the Box Front reference plane", () =>
                fb.EnsurePlane("Box Front", XYZ.BasisY, cy - d, XYZ.BasisX, XYZ.BasisZ, basePoint, halfLength, plan));
            FamilyBuilder.Step("label Depth between Center (Front/Back) and Box Front", () =>
                fb.LabelledDimension(plan, centerY.GetReference(), frontPlane.GetReference(),
                    Line.CreateBound(new XYZ(side, cy, z0), new XYZ(side, cy - d, z0)), pd));
            doc.Regenerate();
            FamilyBuilder.Step("check the Box Front position", () => fb.CheckAt(frontPlane, XYZ.BasisY, cy - d));

            // Height: a Box Top plane h above the level, drawn in the Front elevation, labelled from the level.
            var top = FamilyBuilder.Step("create the Box Top reference plane", () =>
                fb.EnsurePlane("Box Top", XYZ.BasisZ, z0 + h, XYZ.BasisX, XYZ.BasisY, basePoint, halfLength, front));
            FamilyBuilder.Step("label Height between the Ref. Level and Box Top", () =>
                fb.LabelledDimension(front, level.GetPlaneReference(), top.GetReference(),
                    Line.CreateBound(new XYZ(side, cy, z0), new XYZ(side, cy, z0 + h)), ph));
            doc.Regenerate();
            FamilyBuilder.Step("check the Box Top position", () => fb.CheckAt(top, XYZ.BasisZ, z0 + h));

            var extrusion = FamilyBuilder.Step("create the box extrusion on the Ref. Level", () =>
            {
                var sketch = SketchPlane.Create(doc, level.Id);
                var profile = FamilyBuilder.Rectangle(
                    new XYZ(cx - w / 2, cy - d, z0), new XYZ(cx + w / 2, cy - d, z0),
                    new XYZ(cx + w / 2, cy, z0), new XYZ(cx - w / 2, cy, z0));
                return doc.FamilyCreate.NewExtrusion(true, profile, sketch, h);
            });
            doc.Regenerate();

            FamilyBuilder.Step("lock the left face to the Box Left plane", () => fb.Align(plan, x[0].GetReference(), extrusion, -XYZ.BasisX));
            FamilyBuilder.Step("lock the right face to the Box Right plane", () => fb.Align(plan, x[1].GetReference(), extrusion, XYZ.BasisX));
            FamilyBuilder.Step("lock the front face to the Box Front plane", () => fb.Align(plan, frontPlane.GetReference(), extrusion, -XYZ.BasisY));
            FamilyBuilder.Step("lock the back face to the Center (Front/Back) plane", () => fb.Align(plan, centerY.GetReference(), extrusion, XYZ.BasisY));
            FamilyBuilder.Step("lock the top face to the Box Top plane", () => fb.Align(front, top.GetReference(), extrusion, XYZ.BasisZ));

            return new BoxGeometry { Extrusion = extrusion, Cx = cx, Cy = cy, Z0 = z0 };
        }
    }

    internal static class FamilyFactory
    {
        public const string MaterialParameterName = "Structural Material";
        public const string BoxMaterialParameterName = "Material";
        public const string BoxDefaultMaterial = "Plinth Fit-out";

        /// <summary>
        /// Creates a box family (BoxRecipe) from the template, saves it to savePath (never overwriting) and closes it.
        /// When the template makes another category (the generic model template), the family category is set. Throws
        /// FamilyBuildException naming the failed step; the family document is always closed without saving.
        /// </summary>
        public static JObject CreateBox(RevitApplication app, string templatePath, BuiltInCategory category, string savePath, string typeName,
            double w, double d, double h, string materialName)
        {
            Document doc = null;
            try
            {
                doc = FamilyBuilder.Step("open the family template", () => app.NewFamilyDocument(templatePath));
                if (doc == null) throw new FamilyBuildException("open the family template", "Revit returned no family document");
                var family = doc.OwnerFamily ?? throw new FamilyBuildException("open the family template", "the document has no family");
                var fb = new FamilyBuilder(doc);
                string categorySet = null, material;
                int removed;
                BoxGeometry built;
                FamilyParameter pw, pd, ph;
                using (var tx = new Transaction(doc, "Plinth: Build box family"))
                {
                    FamilyBuilder.Step("start the family transaction", () => tx.Start());
                    if (family.FamilyCategory == null || family.FamilyCategory.BuiltInCategory != category)
                        categorySet = FamilyBuilder.Step("set the family category to " + category, () =>
                        {
                            var wanted = Category.GetCategory(doc, category)
                                ?? throw new InvalidOperationException("the family document knows no category " + category);
                            family.FamilyCategory = wanted;
                            if (family.FamilyCategory == null || family.FamilyCategory.BuiltInCategory != category)
                                throw new InvalidOperationException("Revit kept the category " + (family.FamilyCategory?.Name ?? "none"));
                            return wanted.Name;
                        });
                    removed = FamilyBuilder.Step("remove the template's sample geometry", () => fb.RemoveSampleGeometry());
                    FamilyBuilder.Step("create the family type", () => fb.EnsureOnlyType(typeName));
                    pw = FamilyBuilder.Step("add the type parameter Width", () => fb.EnsureLengthParameter(BoxRecipe.Width, w));
                    pd = FamilyBuilder.Step("add the type parameter Depth", () => fb.EnsureLengthParameter(BoxRecipe.Depth, d));
                    ph = FamilyBuilder.Step("add the type parameter Height", () => fb.EnsureLengthParameter(BoxRecipe.Height, h));
                    doc.Regenerate();
                    built = BoxRecipe.Build(fb, pw, pd, ph, w, d, h);
                    // The parameter holds its material before it is associated (an empty material parameter fails regeneration).
                    material = FamilyBuilder.Step("bind the " + BoxMaterialParameterName + " parameter to the extrusion", () =>
                        fb.BindMaterial(built.Extrusion, BoxMaterialParameterName, string.IsNullOrWhiteSpace(materialName) ? BoxDefaultMaterial : materialName.Trim()));
                    var status = FamilyBuilder.Step("commit the family geometry", () => tx.Commit());
                    if (status != TransactionStatus.Committed)
                        throw new FamilyBuildException("commit the family geometry", "Revit rolled the family transaction back (see failures)");
                }

                var flex = BoxFlexCheck(fb, built, pw, pd, ph, w, d, h);

                FamilyBuilder.Step("save the family file", () =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(savePath));
                    doc.SaveAs(savePath, new SaveAsOptions { OverwriteExistingFile = false });
                });

                return new JObject
                {
                    ["template"] = Path.GetFileName(templatePath),
                    ["category_set"] = categorySet,
                    ["material"] = material,
                    ["sample_geometry_removed"] = removed,
                    ["reference_planes_created"] = new JArray(fb.PlanesCreated),
                    ["reference_planes_reused"] = new JArray(fb.PlanesReused),
                    ["flex_check"] = flex
                };
            }
            finally
            {
                Close(doc);
            }
        }

        private static void Close(Document doc)
        {
            if (doc == null) return;
            try
            {
                if (doc.IsValidObject && !doc.Close(false)) AddinLog.Write("Families", "Revit refused to close the new family document.");
            }
            catch (Exception ex)
            {
                AddinLog.Write("Families", "Could not close the new family document: " + ex.Message);
            }
        }

        /// <summary>Flex Width, Depth and Height in a transaction that is always rolled back: the back stays on the origin.</summary>
        private static JObject BoxFlexCheck(FamilyBuilder fb, BoxGeometry built, FamilyParameter pw, FamilyParameter pd, FamilyParameter ph,
            double w, double d, double h)
        {
            const string step = "flex check";
            var doc = fb.Doc;
            double w2 = w + Mm.ToFeet(50), d2 = d + Mm.ToFeet(80), h2 = h + Mm.ToFeet(100);
            using (var tx = new Transaction(doc, "Plinth: Flex check"))
            {
                try
                {
                    FamilyBuilder.Step(step, () => tx.Start());
                    FamilyBuilder.Step(step + ": set Width, Depth and Height", () =>
                    {
                        fb.Manager.Set(pw, w2);
                        fb.Manager.Set(pd, d2);
                        fb.Manager.Set(ph, h2);
                        doc.Regenerate();
                    });
                    var box = FamilyBuilder.Step(step + ": measure", () => FamilyBuilder.Measure(built.Extrusion));
                    Expect(box, XYZ.BasisX, built.Cx - w2 / 2, built.Cx + w2 / 2, "Width");
                    Expect(box, XYZ.BasisY, built.Cy - d2, built.Cy, "Depth (the back staying on the origin)");
                    Expect(box, XYZ.BasisZ, built.Z0, built.Z0 + h2, "Height (the base staying on the level)");
                }
                finally
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                }
            }
            return new JObject { ["width_depth_height_flex"] = true, ["origin"] = "middle of the back edge, at the level" };
        }

        /// <summary>
        /// Creates the family from the template, saves it to savePath (never overwriting) and closes it. Throws
        /// FamilyBuildException naming the failed step; the family document is always closed without saving.
        /// </summary>
        public static JObject Create(RevitApplication app, FamilyRecipe recipe, string templatePath, string savePath, string typeName,
            double b, double h)
        {
            Document doc = null;
            try
            {
                doc = FamilyBuilder.Step("open the family template", () => app.NewFamilyDocument(templatePath));
                if (doc == null) throw new FamilyBuildException("open the family template", "Revit returned no family document");
                var family = doc.OwnerFamily;
                if (family?.FamilyCategory == null || family.FamilyCategory.BuiltInCategory != recipe.Category)
                    throw new FamilyBuildException("check the template category", "the template makes "
                        + (family?.FamilyCategory?.Name ?? "no category") + " families, not " + recipe.Category);

                var fb = new FamilyBuilder(doc);
                int removed;
                string materialType;
                BuiltGeometry built;
                FamilyParameter pb, ph;
                using (var tx = new Transaction(doc, "Plinth: Build family"))
                {
                    FamilyBuilder.Step("start the family transaction", () => tx.Start());
                    materialType = FamilyBuilder.Step("set the structural material type to concrete", () => fb.SetConcrete());
                    removed = FamilyBuilder.Step("remove the template's sample geometry", () => fb.RemoveSampleGeometry());
                    FamilyBuilder.Step("create the family type", () => fb.EnsureOnlyType(typeName));
                    pb = FamilyBuilder.Step("add the type parameter b", () => fb.EnsureLengthParameter(FamilyRecipe.B, b));
                    ph = FamilyBuilder.Step("add the type parameter h", () => fb.EnsureLengthParameter(FamilyRecipe.H, h));
                    doc.Regenerate();
                    built = recipe.Build(fb, pb, ph, b, h);
                    FamilyBuilder.Step("bind the " + MaterialParameterName + " parameter to the extrusion", () => fb.BindMaterial(built.Extrusion));
                    var status = FamilyBuilder.Step("commit the family geometry", () => tx.Commit());
                    if (status != TransactionStatus.Committed)
                        throw new FamilyBuildException("commit the family geometry", "Revit rolled the family transaction back (see failures)");
                }

                var flex = FlexCheck(fb, built, pb, ph, b, h);

                FamilyBuilder.Step("save the family file", () =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(savePath));
                    doc.SaveAs(savePath, new SaveAsOptions { OverwriteExistingFile = false });
                });

                return new JObject
                {
                    ["template"] = recipe.TemplateFileName,
                    ["structural_material_type"] = materialType,
                    ["sample_geometry_removed"] = removed,
                    ["reference_planes_created"] = new JArray(fb.PlanesCreated),
                    ["reference_planes_reused"] = new JArray(fb.PlanesReused),
                    ["parameters_renamed"] = new JArray(fb.ParametersRenamed),
                    ["dimensions_relabelled"] = fb.DimensionsRelabelled,
                    ["flex_check"] = flex
                };
            }
            finally
            {
                Close(doc);
            }
        }

        /// <summary>Flex b, h and the stretch datum in a transaction that is always rolled back; throw if the solid does not follow.</summary>
        private static JObject FlexCheck(FamilyBuilder fb, BuiltGeometry built, FamilyParameter pb, FamilyParameter ph, double b, double h)
        {
            const string step = "flex check";
            var doc = fb.Doc;
            var b2 = b + Mm.ToFeet(50);
            var h2 = h + Mm.ToFeet(80);
            var stretch = Mm.ToFeet(500);
            var datumName = built.StretchDatum.Name;
            bool stretchVerified;
            string stretchNote = null;
            using (var tx = new Transaction(doc, "Plinth: Flex check"))
            {
                try
                {
                    FamilyBuilder.Step(step, () => tx.Start());
                    FamilyBuilder.Step(step + ": set b and h", () =>
                    {
                        fb.Manager.Set(pb, b2);
                        fb.Manager.Set(ph, h2);
                        doc.Regenerate();
                    });
                    var box = FamilyBuilder.Step(step + ": measure", () => FamilyBuilder.Measure(built.Extrusion));
                    Expect(box, built.WidthAxis, built.WidthCenter - b2 / 2, built.WidthCenter + b2 / 2, "b");
                    if (built.DepthCenter.HasValue)
                        Expect(box, built.DepthAxis, built.DepthCenter.Value - h2 / 2, built.DepthCenter.Value + h2 / 2, "h");
                    else
                        Expect(box, built.DepthAxis, built.DepthTop - h2, built.DepthTop, "h");

                    var before = DatumCoordinate(built.StretchDatum, built.StretchAxis);
                    try
                    {
                        ElementTransformUtils.MoveElement(doc, built.StretchDatum.Id, built.StretchAxis * stretch);
                        doc.Regenerate();
                    }
                    catch (Exception ex)
                    {
                        stretchNote = "Revit would not move " + datumName + " in the family: " + ex.Message;
                    }
                    var after = DatumCoordinate(built.StretchDatum, built.StretchAxis);
                    if (stretchNote == null && Math.Abs(after - before - stretch) > FamilyBuilder.Tolerance)
                        stretchNote = datumName + " is held in place in the template, so the stretch could not be checked here.";
                    stretchVerified = stretchNote == null;
                    if (stretchVerified)
                    {
                        box = FamilyBuilder.Step(step + ": measure", () => FamilyBuilder.Measure(built.Extrusion));
                        Expect(box, built.StretchAxis, built.StretchFixed, after, "the stretch to " + datumName);
                    }
                }
                finally
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                }
            }
            return new JObject
            {
                ["b_and_h_flex"] = true,
                ["stretch_datum"] = datumName,
                ["stretch_verified"] = stretchVerified,
                ["stretch_note"] = stretchNote
            };
        }

        private static double DatumCoordinate(Element datum, XYZ axis) =>
            datum is Level level ? level.ProjectElevation * axis.DotProduct(XYZ.BasisZ) : FamilyBuilder.Coordinate((ReferencePlane)datum, axis);

        /// <summary>The solid must span exactly [from, to] along the axis (axis may point either way).</summary>
        private static void Expect(BoundingBoxXYZ box, XYZ axis, double from, double to, string what)
        {
            var a = box.Min.DotProduct(axis);
            var c = box.Max.DotProduct(axis);
            double lo = Math.Min(a, c), hi = Math.Max(a, c);
            double wantLo = Math.Min(from, to), wantHi = Math.Max(from, to);
            if (Math.Abs(lo - wantLo) <= FamilyBuilder.Tolerance && Math.Abs(hi - wantHi) <= FamilyBuilder.Tolerance) return;
            throw new FamilyBuildException("flex check", "the solid does not follow " + what + ": it spans " + Mm.FromFeet(lo) + " to "
                + Mm.FromFeet(hi) + " mm, expected " + Mm.FromFeet(wantLo) + " to " + Mm.FromFeet(wantHi) + " mm");
        }
    }

    /// <summary>Family-document helpers used by the recipes. Everything runs inside the caller's transaction.</summary>
    internal sealed class FamilyBuilder
    {
        public static readonly double Tolerance = Mm.ToFeet(0.1);

        /// <summary>How far reference planes reach past the section and where dimension lines sit.</summary>
        public static readonly double Margin = Mm.ToFeet(300);

        public FamilyBuilder(Document doc)
        {
            Doc = doc;
            Manager = doc.FamilyManager;
        }

        public Document Doc { get; }
        public FamilyManager Manager { get; }
        public List<string> PlanesCreated { get; } = new List<string>();
        public List<string> PlanesReused { get; } = new List<string>();
        public List<string> ParametersRenamed { get; } = new List<string>();
        public int DimensionsRelabelled { get; private set; }

        public static T Step<T>(string step, Func<T> action)
        {
            try
            {
                return action();
            }
            catch (FamilyBuildException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new FamilyBuildException(step, ex.Message);
            }
        }

        public static void Step(string step, Action action) => Step(step, () =>
        {
            action();
            return 0;
        });

        // ---- finding template elements ------------------------------------------------

        /// <summary>A level by name; with onlyLevelFallback, the template's single level when the name differs.</summary>
        /// <summary>
        /// Template names differ between Revit versions and languages in punctuation and case ("Upper Ref. Level" vs
        /// "Upper Ref Level" in the Revit 2026 column template, found live), so names match on letters and digits only.
        /// </summary>
        internal static bool SameName(string a, string b) =>
            a != null && b != null && string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);

        private static string Key(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray());

        public Level FindLevel(string name, bool onlyLevelFallback)
        {
            var levels = new FilteredElementCollector(Doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var level = levels.FirstOrDefault(l => SameName(l.Name, name))
                ?? (onlyLevelFallback && levels.Count == 1 ? levels[0] : null);
            if (level == null)
                throw new InvalidOperationException("the template has no level named '" + name + "' (levels: " + Names(levels.Select(l => l.Name)) + ")");
            return level;
        }

        public ViewPlan FindPlanView(Level level)
        {
            var views = new FilteredElementCollector(Doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType != ViewType.CeilingPlan && v.GenLevel != null && v.GenLevel.Id == level.Id).ToList();
            var view = views.FirstOrDefault(v => SameName(v.Name, level.Name)) ?? views.FirstOrDefault();
            if (view == null) throw new InvalidOperationException("the template has no plan view of '" + level.Name + "'");
            return view;
        }

        /// <summary>An elevation by name, else the one looking along the given axis.</summary>
        public Autodesk.Revit.DB.View FindElevation(string name, XYZ lookAxis)
        {
            var views = new FilteredElementCollector(Doc).OfClass(typeof(ViewSection)).Cast<Autodesk.Revit.DB.View>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Elevation).ToList();
            var view = views.FirstOrDefault(v => SameName(v.Name, name))
                ?? views.FirstOrDefault(v => Parallel(v.ViewDirection, lookAxis));
            if (view == null)
                throw new InvalidOperationException("the template has no '" + name + "' elevation (elevations: " + Names(views.Select(v => v.Name)) + ")");
            if (!Parallel(view.ViewDirection, lookAxis))
                throw new InvalidOperationException("the '" + view.Name + "' elevation does not look along the expected axis");
            return view;
        }

        /// <summary>A reference plane by name; null when absent; an error when it faces the wrong way.</summary>
        public ReferencePlane FindPlane(string name, XYZ axis)
        {
            var plane = Planes().FirstOrDefault(p => SameName(p.Name, name));
            if (plane != null && !Parallel(plane.Normal, axis))
                throw new InvalidOperationException("the template's reference plane '" + plane.Name + "' is not perpendicular to the expected axis");
            return plane;
        }

        public ReferencePlane RequiredPlane(string name, XYZ axis) =>
            FindPlane(name, axis) ?? throw new InvalidOperationException(
                "the template has no reference plane named '" + name + "' (planes: " + Names(Planes().Select(p => p.Name)) + ")");

        /// <summary>A centre plane by name, else the plane through the origin perpendicular to the axis.</summary>
        public ReferencePlane CenterPlane(string name, XYZ axis) =>
            FindPlane(name, axis)
            ?? Planes().FirstOrDefault(p => Parallel(p.Normal, axis) && Math.Abs(Coordinate(p, axis)) <= Tolerance)
            ?? throw new InvalidOperationException("the template has no reference plane '" + name + "' or other plane through the origin "
                + "perpendicular to that axis (planes: " + Names(Planes().Select(p => p.Name)) + ")");

        private IEnumerable<ReferencePlane> Planes() =>
            new FilteredElementCollector(Doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>();

        // ---- types and parameters -----------------------------------------------------

        /// <summary>Leaves exactly one family type, named typeName, as the current type.</summary>
        public void EnsureOnlyType(string typeName)
        {
            var named = Types().FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.Ordinal));
            if (named != null)
                Manager.CurrentType = named;
            else if (Manager.CurrentType == null && Types().Count == 0)
                Manager.NewType(typeName);
            else
            {
                if (Manager.CurrentType == null) Manager.CurrentType = Types()[0];
                Manager.RenameCurrentType(typeName);
            }
            foreach (var other in Types().Where(t => !string.Equals(t.Name, typeName, StringComparison.Ordinal)).Select(t => t.Name).ToList())
            {
                Manager.CurrentType = Types().First(t => t.Name == other);
                Manager.DeleteCurrentType();
            }
            Manager.CurrentType = Types().First(t => t.Name == typeName);
        }

        private List<FamilyType> Types() => Manager.Types.Cast<FamilyType>().ToList();

        /// <summary>
        /// The length type parameter b or h, set to value. A template parameter with the alternative name (Width, Depth)
        /// is renamed rather than duplicated, so dimensions it already labels keep working.
        /// </summary>
        public FamilyParameter EnsureLengthParameter(SectionDimension dimension, double value)
        {
            var parameter = FindParameter(dimension.Parameter);
            if (parameter == null)
            {
                var alternative = FindParameter(dimension.Alternative);
                if (alternative != null && !alternative.IsShared && !IsBuiltIn(alternative) && IsLength(alternative))
                {
                    Manager.RenameParameter(alternative, dimension.Parameter);
                    ParametersRenamed.Add(dimension.Alternative + " -> " + dimension.Parameter);
                    parameter = alternative;
                }
            }
            if (parameter == null) parameter = Manager.AddParameter(dimension.Parameter, GroupTypeId.Geometry, SpecTypeId.Length, false);
            if (!IsLength(parameter)) throw new InvalidOperationException("the template's parameter '" + dimension.Parameter + "' is not a length");
            if (!string.IsNullOrEmpty(parameter.Formula))
                throw new InvalidOperationException("the template's parameter '" + dimension.Parameter + "' is driven by a formula");
            if (parameter.IsInstance) Manager.MakeType(parameter);
            Manager.Set(parameter, value);
            return parameter;
        }

        /// <summary>
        /// Associates the extrusion's material with a type material parameter (by default "Structural Material" holding
        /// Concrete), adding it when missing. Returns the material the type holds.
        /// </summary>
        public string BindMaterial(Extrusion extrusion, string parameterName = FamilyFactory.MaterialParameterName, string materialName = "Concrete")
        {
            var parameter = FindParameter(parameterName);
            if (parameter == null)
                parameter = Manager.AddParameter(parameterName, GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
            else
            {
                if (!SpecTypeId.Reference.Material.Equals(parameter.Definition.GetDataType()))
                    throw new InvalidOperationException("the template's parameter '" + parameter.Definition.Name + "' is not a material parameter");
                if (parameter.IsInstance && !parameter.IsShared && !IsBuiltIn(parameter)) Manager.MakeType(parameter);
            }
            var material = extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
            if (material == null || !Manager.CanElementParameterBeAssociated(material))
                throw new InvalidOperationException("the extrusion's material cannot be bound to a family parameter");
            // Binding geometry to a material parameter that holds no material fails with "Document regeneration
            // failed" (the beam template's built-in Structural Material starts empty; found live). Give it the material.
            var held = EnsureMaterialValue(parameter, materialName);
            Manager.AssociateElementParameterToFamilyParameter(material, parameter);
            return held;
        }

        private string EnsureMaterialValue(FamilyParameter parameter, string materialName)
        {
            var current = Manager.CurrentType?.AsElementId(parameter);
            if (current != null && current != ElementId.InvalidElementId && Doc.GetElement(current) is Material held) return held.Name;
            var found = new FilteredElementCollector(Doc).OfClass(typeof(Material)).Cast<Material>()
                .FirstOrDefault(m => string.Equals(m.Name, materialName, StringComparison.OrdinalIgnoreCase));
            var id = found?.Id ?? Material.Create(Doc, materialName);
            Manager.Set(parameter, id);
            return ((Material)Doc.GetElement(id)).Name;
        }

        private FamilyParameter FindParameter(string name)
        {
            var all = Manager.Parameters.Cast<FamilyParameter>().Where(p => p.Definition != null).ToList();
            return all.FirstOrDefault(p => p.Definition.Name == name)
                ?? all.FirstOrDefault(p => string.Equals(p.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsLength(FamilyParameter p) => SpecTypeId.Length.Equals(p.Definition.GetDataType());

        internal static bool IsBuiltIn(FamilyParameter p) =>
            p.Definition is InternalDefinition definition && definition.BuiltInParameter != BuiltInParameter.INVALID;

        // ---- geometry and constraints -------------------------------------------------

        /// <summary>
        /// Family.StructuralMaterialType is read-only in the API, so the family's Structural Material Type parameter is set.
        /// Returns what the family ends up with ("Concrete", or why it is unchanged).
        /// </summary>
        public string SetConcrete()
        {
            var family = Doc.OwnerFamily;
            var parameter = family.get_Parameter(BuiltInParameter.FAMILY_STRUCT_MATERIAL_TYPE);
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.Integer)
                return "unchanged (" + family.StructuralMaterialType + "): the template does not expose it to the API";
            if (!parameter.Set((int)StructuralMaterialType.Concrete)) return "unchanged (" + family.StructuralMaterialType + "): Revit refused";
            return family.StructuralMaterialType.ToString();
        }

        /// <summary>Deletes every solid or void form the template ships with; the recipe draws its own.</summary>
        public int RemoveSampleGeometry()
        {
            var ids = new FilteredElementCollector(Doc).WhereElementIsNotElementType().Where(e => e is GenericForm).Select(e => e.Id).ToList();
            if (ids.Count > 0) Doc.Delete(ids);
            return ids.Count;
        }

        /// <summary>
        /// Two planes size apart, symmetric about the centre plane: an EQ dimension across both halves and a dimension
        /// labelled with the parameter across the pair. Existing planes of the same name are reused; a dimension already
        /// spanning the pair is relabelled. Returns [negative side, positive side].
        /// </summary>
        public ReferencePlane[] SymmetricPair(string parameterName, Autodesk.Revit.DB.View view, ReferencePlane center, XYZ axis, string negName, string posName,
            double size, FamilyParameter parameter, XYZ along, XYZ cut, XYZ basePoint, double halfLength, double dimensionOffset)
        {
            var c = Coordinate(center, axis);
            var neg = Step("create the " + negName + " reference plane", () => EnsurePlane(negName, axis, c - size / 2, along, cut, basePoint, halfLength, view));
            var pos = Step("create the " + posName + " reference plane", () => EnsurePlane(posName, axis, c + size / 2, along, cut, basePoint, halfLength, view));
            Step("make " + negName + " and " + posName + " equal about " + center.Name, () =>
                EqualDimension(view, neg, center, pos, DimensionLine(basePoint, axis, c, size, along, dimensionOffset)));
            Step("label " + parameterName + " between " + negName + " and " + posName, () =>
                LabelledDimension(view, neg.GetReference(), pos.GetReference(),
                    DimensionLine(basePoint, axis, c, size, along, dimensionOffset - Margin / 2), parameter));
            Doc.Regenerate();
            Step("check the " + negName + " and " + posName + " positions", () =>
            {
                CheckAt(neg, axis, c - size / 2);
                CheckAt(pos, axis, c + size / 2);
            });
            return new[] { neg, pos };
        }

        /// <summary>The named plane if the template has it, else a new one at the coordinate along the axis.</summary>
        public ReferencePlane EnsurePlane(string name, XYZ axis, double coordinate, XYZ along, XYZ cut, XYZ basePoint, double halfLength, Autodesk.Revit.DB.View view)
        {
            var existing = FindPlane(name, axis);
            if (existing != null)
            {
                PlanesReused.Add(name);
                return existing;
            }
            var origin = basePoint + axis * (coordinate - basePoint.DotProduct(axis));
            var plane = Doc.FamilyCreate.NewReferencePlane(origin + along * halfLength, origin - along * halfLength, cut, view);
            plane.Name = name;
            PlanesCreated.Add(name);
            // A plane made in this transaction has no geometric reference until the document regenerates; dimensioning
            // it before that fails with "The references are not geometric references" (found in the first live run).
            Doc.Regenerate();
            return plane;
        }

        public void CheckAt(ReferencePlane plane, XYZ axis, double coordinate)
        {
            var actual = Coordinate(plane, axis);
            if (Math.Abs(actual - coordinate) > Tolerance)
                throw new InvalidOperationException("reference plane '" + plane.Name + "' is at " + Mm.FromFeet(actual) + " mm, expected "
                    + Mm.FromFeet(coordinate) + " mm");
        }

        public void LabelledDimension(Autodesk.Revit.DB.View view, Reference a, Reference b, Line line, FamilyParameter parameter)
        {
            var existing = FindDimension(new[] { a, b });
            if (existing == null)
            {
                var refs = new ReferenceArray();
                refs.Append(a);
                refs.Append(b);
                existing = Doc.FamilyCreate.NewDimension(view, line, refs)
                    ?? throw new InvalidOperationException("Revit did not create the dimension");
            }
            else if (existing.FamilyLabel != null && existing.FamilyLabel.Id == parameter.Id)
                return;
            else
                DimensionsRelabelled++;
            existing.FamilyLabel = parameter;
        }

        public void EqualDimension(Autodesk.Revit.DB.View view, ReferencePlane a, ReferencePlane center, ReferencePlane b, Line line)
        {
            var refs = new[] { a.GetReference(), center.GetReference(), b.GetReference() };
            var existing = FindDimension(refs);
            if (existing == null)
            {
                var array = new ReferenceArray();
                foreach (var r in refs) array.Append(r);
                existing = Doc.FamilyCreate.NewDimension(view, line, array)
                    ?? throw new InvalidOperationException("Revit did not create the EQ dimension");
            }
            if (!existing.AreSegmentsEqual) existing.AreSegmentsEqual = true;
        }

        /// <summary>A linear dimension whose references are exactly these elements (any order).</summary>
        private Dimension FindDimension(IList<Reference> refs)
        {
            var wanted = new HashSet<long>(refs.Select(r => RevitCompat.GetId(r.ElementId)));
            foreach (var dimension in new FilteredElementCollector(Doc).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                if (dimension.DimensionShape != DimensionShape.Linear) continue;
                ReferenceArray existing;
                try { existing = dimension.References; }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
                if (existing == null || existing.Size != refs.Count) continue;
                var ids = new HashSet<long>(existing.Cast<Reference>().Select(r => RevitCompat.GetId(r.ElementId)));
                if (ids.SetEquals(wanted)) return dimension;
            }
            return null;
        }

        private static Line DimensionLine(XYZ basePoint, XYZ axis, double center, double size, XYZ along, double offset)
        {
            var origin = basePoint + axis * (center - basePoint.DotProduct(axis)) + along * offset;
            return Line.CreateBound(origin - axis * size, origin + axis * size);
        }

        /// <summary>Locks the extrusion face whose outward normal is faceNormal to a plane or level already on it.</summary>
        public void Align(Autodesk.Revit.DB.View view, Reference datum, Extrusion extrusion, XYZ faceNormal)
        {
            var face = FindFace(extrusion, faceNormal);
            if (Doc.FamilyCreate.NewAlignment(view, datum, face.Reference) == null)
                throw new InvalidOperationException("Revit did not create the alignment");
            // Regenerate now, so a lock that conflicts with the others fails at its own step instead of at the next
            // regeneration (the first live run reported "Document regeneration failed" at the material binding).
            Doc.Regenerate();
        }

        private static PlanarFace FindFace(Extrusion extrusion, XYZ normal)
        {
            var options = new Options { ComputeReferences = true };
            foreach (var solid in extrusion.get_Geometry(options).OfType<Solid>().Where(s => s.Volume > 0))
                foreach (Face face in solid.Faces)
                    if (face is PlanarFace planar && planar.Reference != null && planar.FaceNormal.IsAlmostEqualTo(normal))
                        return planar;
            throw new InvalidOperationException("the extrusion has no planar face with normal " + normal);
        }

        public static BoundingBoxXYZ Measure(Extrusion extrusion)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var any = false;
            foreach (var solid in extrusion.get_Geometry(new Options()).OfType<Solid>().Where(s => s.Volume > 0))
                foreach (Edge edge in solid.Edges)
                    foreach (var p in edge.Tessellate())
                    {
                        any = true;
                        minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
                        maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
                    }
            if (!any) throw new InvalidOperationException("the extrusion has no solid geometry");
            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
        }

        public static CurveArrArray Rectangle(XYZ p0, XYZ p1, XYZ p2, XYZ p3)
        {
            var loop = new CurveArray();
            loop.Append(Line.CreateBound(p0, p1));
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p0));
            var profile = new CurveArrArray();
            profile.Append(loop);
            return profile;
        }

        /// <summary>Where the plane sits along the axis (the plane must be perpendicular to it).</summary>
        public static double Coordinate(ReferencePlane plane, XYZ axis) => plane.GetPlane().Origin.DotProduct(axis);

        private static bool Parallel(XYZ a, XYZ b) => Math.Abs(Math.Abs(a.Normalize().DotProduct(b.Normalize())) - 1) < 1e-9;

        private static string Names(IEnumerable<string> names) => string.Join(", ", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(30));
    }
}
