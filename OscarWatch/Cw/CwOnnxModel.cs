using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OscarWatch.Core.CwDecode;

namespace OscarWatch.Cw;

/// <summary>The DeepCW model, run in process on the CPU through ONNX Runtime.</summary>
public sealed class CwOnnxModel : ICwModelRunner, IDisposable
{
    public const string ModelFileName = "model.onnx";
    public const string MetadataFileName = "model.onnx.json";

    private readonly InferenceSession _session;

    private CwOnnxModel(InferenceSession session, CwModelMetadata metadata)
    {
        _session = session;
        Metadata = metadata;
    }

    /// <summary>Folder the build copies the model into, beside the executable.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "deepcw");

    public CwModelMetadata Metadata { get; }

    public static CwOnnxModel Load(string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        var metadata = CwModelMetadata.Load(Path.Combine(dir, MetadataFileName));
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            // Leave cores for the tracker, rig control and other decoders on small machines.
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
        };
        var session = new InferenceSession(Path.Combine(dir, ModelFileName), options);
        return new CwOnnxModel(session, metadata);
    }

    public float[] Run(float[] spectrogram, int frames, int bins, out int outputFrames)
    {
        var input = new DenseTensor<float>(spectrogram, [1, 1, frames, bins]);
        using var results = _session.Run(
            [NamedOnnxValue.CreateFromTensor(Metadata.OnnxInputName, input)],
            [Metadata.OnnxOutputName]);
        var output = results[0].AsTensor<float>();
        var dims = output.Dimensions;
        if (dims.Length != 3 || dims[2] != Metadata.NumClasses)
            throw new InvalidDataException($"Unexpected CW model output shape [{string.Join(", ", dims.ToArray())}].");

        outputFrames = dims[1];
        return output.ToArray();
    }

    public void Dispose() => _session.Dispose();
}
