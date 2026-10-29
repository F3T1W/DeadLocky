using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using ValveResourceFormat.ResourceTypes.ModelData.Attachments;
using SourceModel = ValveResourceFormat.ResourceTypes.Model;

namespace DeadLocky.Features.ManageMods.Api;

internal static class ModelCameraRepair
{
    private static readonly string[] CameraSections =
        ["AttachmentCameraData", "CitadelCameraSettings_t", "CitadelModelSceneData_t"];
    private static readonly string[] CameraAttachments =
    [
        "standing_pivot", "crouching_pivot", "portrait_camera", "portrait_camera_attach",
        "near_00", "near_01", "near_02", "far_00", "far_01", "far_02",
        "gunaim_00", "gunaim_01", "gunaim_02", "aim", "root_aim", "lean", "zipline", "ability_apply_ground"
    ];

    public static byte[]? Repair(byte[] source, byte[] baseline, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateContainer(source);
        ValidateContainer(baseline);
        using var mod = new Resource();
        using var game = new Resource();
        using var modStream = new MemoryStream(source, false);
        using var gameStream = new MemoryStream(baseline, false);
        mod.Read(modStream);
        game.Read(gameStream);
        if (mod.DataBlock is not SourceModel model || game.DataBlock is not SourceModel reference
            || !reference.Attachments.ContainsKey("standing_pivot")
            || !reference.Attachments.ContainsKey("crouching_pivot"))
        {
            return null;
        }

        var names = new HashSet<string>(CameraAttachments, StringComparer.Ordinal);
        if (reference.KeyValues.TryGetValue("AttachmentCameraData", out KVObject? cameras))
        {
            foreach (KVObject camera in cameras.Values)
            {
                _ = names.Add((string)camera["attachment_name"]);
            }
        }

        bool missingMetadata = reference.KeyValues.ContainsKey("AttachmentCameraData")
                               && !model.KeyValues.ContainsKey("AttachmentCameraData");
        bool oldAxis = HasOldPivot("standing_pivot") && HasOldPivot("crouching_pivot");
        bool missingPivots = !model.Attachments.ContainsKey("standing_pivot")
                             && !model.Attachments.ContainsKey("crouching_pivot")
                             && model.KeyValues.ContainsKey("CCitadelHeroModelGameData_t");
        if (!missingMetadata && !oldAxis && !missingPivots)
        {
            return null;
        }

        Bone? root = model.Skeleton.Bones.SingleOrDefault(static bone => bone.Name == "root_motion");
        Bone? donorRoot = reference.Skeleton.Bones.SingleOrDefault(static bone => bone.Name == "root_motion");
        if (root is null || donorRoot is null || root.Parent is not null || donorRoot.Parent is not null
            || Vector3.Distance(root.Position, donorRoot.Position) > 0.001f
            || Math.Abs(Quaternion.Dot(root.Angle, donorRoot.Angle)) < 0.99999f)
        {
            throw new IOException("The model uses an incompatible root bone; automatic camera repair is unavailable.");
        }

        var bones = model.Skeleton.Bones.Select(static bone => bone.Name).ToHashSet(StringComparer.Ordinal);
        var replacements = new Dictionary<string, KVObject>(StringComparer.Ordinal);
        foreach (Mesh mesh in game.Blocks.OfType<Mesh>())
        {
            foreach (KVObject attachment in mesh.Data["m_attachments"].Values)
            {
                string name = (string)attachment["key"];
                if (!names.Contains(name) || replacements.ContainsKey(name))
                {
                    continue;
                }

                KVObject value = attachment["value"];
                int influences = (int)value["m_nInfluences"];
                for (int i = 0; i < influences; i++)
                {
                    string bone = (string)value["m_influenceNames"][i];
                    if ((double)value["m_influenceWeights"][i] > 0 && !bones.Contains(bone))
                    {
                        throw new IOException($"Camera attachment {name} needs a missing bone ({bone}).");
                    }
                }

                replacements.Add(name, attachment);
            }
        }

        if (!replacements.ContainsKey("standing_pivot") || !replacements.ContainsKey("crouching_pivot"))
        {
            throw new IOException("The current game model has no supported embedded camera attachments.");
        }

        foreach (Mesh mesh in mod.Blocks.OfType<Mesh>())
        {
            token.ThrowIfCancellationRequested();
            KVObject attachments = mesh.Data["m_attachments"];
            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (KVObject attachment in attachments.Values)
            {
                string name = (string)attachment["key"];
                _ = existing.Add(name);
                if (replacements.TryGetValue(name, out KVObject? replacement))
                {
                    attachment["value"] = replacement["value"];
                }
            }

            foreach ((string name, KVObject attachment) in replacements)
            {
                if (!existing.Contains(name))
                {
                    attachments.Add(attachment);
                }
            }
        }

        KVObject info = model.Data["m_modelInfo"];
        string text = (string)info["m_keyValueText"];
        string donorText = (string)reference.Data["m_modelInfo"]["m_keyValueText"];
        text = CameraSections.Aggregate(text, (current, section) => ReplaceSection(current, donorText, section));

        info["m_keyValueText"] = text;
        byte[] repaired = Serialize(mod, source, token);
        Verify(source, repaired, baseline, names, replacements, token);
        return repaired;

        bool HasOldPivot(string name)
        {
            return model.Attachments.TryGetValue(name, out Attachment? actual) && actual.Length > 0
                   && reference.Attachments.TryGetValue(name, out Attachment? expected) && expected.Length > 0
                   && actual[0].Name == expected[0].Name
                   && Math.Abs(Quaternion.Dot(actual[0].Rotation, expected[0].Rotation)) < 0.95f
                   && Math.Abs(expected[0].Offset.Z) > 20 && Math.Abs(actual[0].Offset.Y) > 20
                   && Math.Abs(actual[0].Offset.Z) < 5
                   && Math.Abs(Math.Abs(actual[0].Offset.Y) - Math.Abs(expected[0].Offset.Z)) < 10;
        }
    }

    internal static string ReplaceSection(string text, string donor, string name)
    {
        (int start, int length) = Section(donor, name);
        if (start < 0)
        {
            return text;
        }

        (int oldStart, int oldLength) = Section(text, name);
        if (oldStart >= 0)
        {
            return text.Remove(oldStart, oldLength).Insert(oldStart, donor.Substring(start, length));
        }

        int end = text.LastIndexOf('}');
        return end < 0
            ? throw new IOException("Unsupported model camera metadata.")
            : text.Insert(end, string.Concat(donor.AsSpan(start, length), "\n"));
    }

    private static (int Start, int Length) Section(string text, string name)
    {
        int start = text.IndexOf("\t" + name + " =", StringComparison.Ordinal);
        if (start < 0)
        {
            return (-1, 0);
        }

        int value = text.IndexOf('=', start) + 1;
        while (value < text.Length && char.IsWhiteSpace(text[value]))
        {
            value++;
        }

        if (value >= text.Length || text[value] is not ('{' or '['))
        {
            throw new IOException("Unsupported camera metadata section.");
        }

        char open = text[value];
        char close = open == '{' ? '}' : ']';
        int depth = 0;
        bool quoted = false;
        bool escape = false;
        for (int i = value; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (escape) { escape = false; }
                else
                {
                    switch (c)
                    {
                        case '\\': escape = true; break;
                        case '"': quoted = false; break;
                        default: continue;
                    }
                }
            }
            else if (c == '"') { quoted = true; }
            else if (c == open) { depth++; }
            else if (c == close && --depth == 0) { return (start, i + 1 - start); }
        }

        throw new IOException("Truncated camera metadata section.");
    }

    private static byte[] Serialize(Resource model, byte[] original, CancellationToken token)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(0u);
        writer.Write((ushort)12);
        writer.Write(BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(6)));
        writer.Write(8u);
        writer.Write((uint)model.Blocks.Count);
        writer.Write(new byte[model.Blocks.Count * 12]);
        for (int i = 0; i < model.Blocks.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            Block block = model.Blocks[i];
            while (output.Position % 16 != 0) { writer.Write((byte)0); }
            long offset = output.Position;
            string type = block.Type.ToString();
            if (type is "DATA" or "MDAT") { block.Serialize(output); }
            else { writer.Write(original, (int)block.Offset, (int)block.Size); }
            long end = output.Position;
            output.Position = 16 + (i * 12);
            writer.Write(Encoding.ASCII.GetBytes(type));
            writer.Write(checked((uint)(offset - output.Position)));
            writer.Write(checked((uint)(end - offset)));
            output.Position = end;
        }

        output.Position = 0;
        writer.Write(checked((uint)output.Length));
        return output.ToArray();
    }

    private static void Verify(byte[] source, byte[] repaired, byte[] baseline, HashSet<string> names,
        Dictionary<string, KVObject> replacements, CancellationToken token)
    {
        using var before = new Resource();
        using var after = new Resource();
        using var game = new Resource();
        using var beforeStream = new MemoryStream(source, false);
        using var afterStream = new MemoryStream(repaired, false);
        using var gameStream = new MemoryStream(baseline, false);
        before.Read(beforeStream);
        after.Read(afterStream);
        game.Read(gameStream);
        SourceModel old = before.DataBlock as SourceModel ?? throw new IOException("Invalid original model.");
        SourceModel current = after.DataBlock as SourceModel ?? throw new IOException("Invalid repaired model.");
        SourceModel donor = game.DataBlock as SourceModel ?? throw new IOException("Invalid game model.");
        if (Fingerprint(old.Data, names) != Fingerprint(current.Data, names)
            || Metadata(old.KeyValues) != Metadata(current.KeyValues) || before.Blocks.Count != after.Blocks.Count)
        {
            throw new IOException("Camera repair changed unrelated model data; the original was retained.");
        }

        foreach (string section in CameraSections.Where(donor.KeyValues.ContainsKey))
        {
            if (!current.KeyValues.TryGetValue(section, out KVObject? value)
                || Fingerprint(value, names) != Fingerprint(donor.KeyValues[section], names))
            {
                throw new IOException("Camera metadata verification failed.");
            }
        }

        int meshes = 0;
        for (int i = 0; i < before.Blocks.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            Block left = before.Blocks[i];
            Block right = after.Blocks[i];
            if (left.Type != right.Type) { throw new IOException("Model block types changed."); }
            if (left is Mesh mesh && right is Mesh fixedMesh)
            {
                meshes++;
                if (Fingerprint(mesh.Data, names) != Fingerprint(fixedMesh.Data, names))
                {
                    throw new IOException("Camera repair changed mesh geometry or other attachments.");
                }

                var attachments = fixedMesh.Data["m_attachments"].Values
                    .ToDictionary(static entry => (string)entry["key"], static entry => entry["value"]);
                foreach ((string name, KVObject attachment) in replacements)
                {
                    if (!attachments.TryGetValue(name, out KVObject? actual)
                        || Fingerprint(actual, names) != Fingerprint(attachment["value"], names))
                    {
                        throw new IOException($"Camera attachment verification failed: {name}.");
                    }
                }
            }
            else if (left.Type.ToString() != "DATA"
                     && !source.AsSpan((int)left.Offset, (int)left.Size)
                         .SequenceEqual(repaired.AsSpan((int)right.Offset, (int)right.Size)))
            {
                throw new IOException("Camera repair changed an unrelated model block.");
            }
        }

        if (meshes == 0) { throw new IOException("Models without embedded meshes are unsupported."); }
        return;

        string Metadata(KVObject data)
        {
            return string.Join(';', data.Children
                .Where(static pair => !CameraSections.Contains(pair.Key, StringComparer.Ordinal))
                .Select(pair => pair.Key + ":" + Fingerprint(pair.Value, names)));
        }
    }

    private static string Fingerprint(KVObject value, HashSet<string> names, string path = "")
    {
        if (path == "m_modelInfo/m_keyValueText") { return "<camera metadata>"; }
        if (!value.IsCollection)
        {
            return value.ValueType + ":" + value.Flag + ":" + value.ToString(CultureInfo.InvariantCulture);
        }

        var text = new StringBuilder(value.ValueType.ToString());
        foreach ((string key, KVObject child) in value.Children)
        {
            if (path.Length == 0 && key == "m_attachments")
            {
                foreach (KVObject attachment in child.Values.Where(entry => !names.Contains((string)entry["key"])))
                {
                    _ = text.Append(Fingerprint(attachment, names));
                }
            }
            else
            {
                _ = text.Append(key.Length).Append(':').Append(key).Append('=')
                    .Append(Fingerprint(child, names, path.Length == 0 ? key : path + "/" + key)).Append(';');
            }
        }

        return text.ToString();
    }

    private static void ValidateContainer(byte[] bytes)
    {
        if (bytes.Length < 16 || bytes.Length > 64 * 1024 * 1024
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != bytes.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != 12)
        {
            throw new IOException("Unsupported compiled model container.");
        }

        long table = 8L + BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        if (table < 16 || count is 0 or > 4096 || table + (count * 12L) > bytes.Length)
        {
            throw new IOException("Invalid compiled model block table.");
        }

        for (int i = 0; i < count; i++)
        {
            int record = (int)table + (i * 12);
            long offset = record + 4L + BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(record + 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(record + 8));
            if (offset < table + (count * 12L) || offset + size > bytes.Length)
            {
                throw new IOException("Invalid compiled model block range.");
            }
        }
    }
}
