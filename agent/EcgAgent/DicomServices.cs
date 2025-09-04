using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client; // not strictly needed here
using Microsoft.Extensions.Logging;

public class EcgDicomService :
    DicomService,
    IDicomServiceProvider,
    IDicomCEchoProvider,
    IDicomCFindProvider,
    IDicomCStoreProvider
{
    private readonly ILogger<EcgDicomService> _log;
    private static readonly HashSet<DicomUID> _acceptedStorage = new()
    {
        DicomUID.EncapsulatedPDFStorage,
        DicomUID.TwelveLeadECGWaveformStorage,        // optional – raw ECG waveform
        DicomUID.SecondaryCaptureImageStorage         // optional – some devices send SC
    };

    public EcgDicomService(INetworkStream stream, Encoding fallback, ILogger log,
                           DicomServiceDependencies deps)
        : base(stream, fallback, log, deps)
    {
        _log = deps.LoggerFactory.CreateLogger<EcgDicomService>();
    }

    // --- Association negotiation: choose what we accept ---
    public void OnReceiveAssociationRequest(DicomAssociation association)
    {
        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification ||
                pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFIND ||
                _acceptedStorage.Contains(pc.AbstractSyntax))
            {
                pc.SetResult(DicomPresentationContextResult.Accept);
            }
            else
            {
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
            }
        }
        SendAssociationAccept(association);
    }

    public void OnReceiveAssociationReleaseRequest() => SendAssociationReleaseResponse();
    public void OnConnectionClosed(Exception ex) { /* log if needed */ }
    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }

    // --- C-ECHO ---
    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest req)
        => Task.FromResult(new DicomCEchoResponse(req, DicomStatus.Success));
    // (See fo-dicom IDicomCEchoProvider) :contentReference[oaicite:1]{index=1}

    // --- MWL C-FIND ---
    // Return zero or more "Pending" responses, then one "Success".
    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        // Filter your in-memory MWL by the incoming keys
        foreach (var match in WorklistStore.Query(request.Dataset))
        {
            yield return new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = match };
        }
        yield return new DicomCFindResponse(request, DicomStatus.Success);
    }
    // (v5 uses IAsyncEnumerable for C-FIND; example pattern) :contentReference[oaicite:2]{index=2}

    // --- C-STORE ---
    public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest req)
    {
        try
        {
            // Persist the received instance (fo-dicom creates a temp file; you can also get the DicomFile)
            var file = await DicomService.GetCStoreDicomFile(req).ConfigureAwait(false); // v5 helper
            var studyUid = file.Dataset.GetString(DicomTag.StudyInstanceUID);
            var seriesUid = file.Dataset.GetString(DicomTag.SeriesInstanceUID);
            var sopUid = file.Dataset.GetString(DicomTag.SOPInstanceUID);

            // Save DICOM to your landing directory
            var root = Path.Combine("C:\\ecg-poc\\dicom-in");
            Directory.CreateDirectory(root);
            var dcmPath = Path.Combine(root, $"{studyUid}_{seriesUid}_{sopUid}.dcm");
            await file.SaveAsync(dcmPath);

            // If it's Encapsulated PDF, also extract PDF bytes so your existing UI can render it
            if (req.SOPClassUID == DicomUID.EncapsulatedPDFStorage)
            {
                var pdfBytes = file.Dataset.GetValue<byte[]>(DicomTag.EncapsulatedDocument, 0);
                var pdfName = $"{studyUid}_{seriesUid}_{sopUid}.pdf";
                var pdfOut = Path.Combine("C:\\ecg-poc\\ingest", pdfName); // your existing ingest
                Directory.CreateDirectory(Path.GetDirectoryName(pdfOut)!);
                await File.WriteAllBytesAsync(pdfOut, pdfBytes);
                // (Your existing file-watcher/Azurite uploader will pick this up unchanged)
            }

            return new DicomCStoreResponse(req, DicomStatus.Success);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "C-STORE failed");
            return new DicomCStoreResponse(req, DicomStatus.ProcessingFailure);
        }
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
        => Task.CompletedTask;
}
