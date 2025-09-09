using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client; // not strictly needed here
using Microsoft.Extensions.Logging;
using System.Text; // needed for Encoding

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
        DicomUID.TwelveLeadECGWaveformStorage,
        DicomUID.SecondaryCaptureImageStorage
    };

    public EcgDicomService(INetworkStream stream, Encoding fallback, ILogger log,
                           DicomServiceDependencies deps)
        : base(stream, fallback, log, deps)
    {
        _log = deps.LoggerFactory.CreateLogger<EcgDicomService>();
    }

    // --- Association negotiation (v5 async interface) ---
    public async Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification ||
                pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind ||
                _acceptedStorage.Contains(pc.AbstractSyntax))
            {
                pc.SetResult(DicomPresentationContextResult.Accept);
            }
            else
            {
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
            }
        }
        await SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync() => SendAssociationReleaseResponseAsync();
    public void OnConnectionClosed(Exception exception) { }
    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }

    // --- C-ECHO ---
    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest req)
        => Task.FromResult(new DicomCEchoResponse(req, DicomStatus.Success));

    // --- MWL C-FIND ---
    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        foreach (var match in WorklistStore.Query(request.Dataset))
        {
            yield return new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = match };
        }
        yield return new DicomCFindResponse(request, DicomStatus.Success);
        await Task.CompletedTask;
    }

    // --- C-STORE ---
    public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest req)
    {
        try
        {
            var file = GetCStoreDicomFile(); // parameterless protected method returns DicomFile
            var studyUid = file.Dataset.GetString(DicomTag.StudyInstanceUID);
            var seriesUid = file.Dataset.GetString(DicomTag.SeriesInstanceUID);
            var sopUid = file.Dataset.GetString(DicomTag.SOPInstanceUID);

            var root = Path.Combine("C:\\ecg-poc\\dicom-in");
            Directory.CreateDirectory(root);
            var dcmPath = Path.Combine(root, $"{studyUid}_{seriesUid}_{sopUid}.dcm");
            file.Save(dcmPath);

            if (req.SOPClassUID == DicomUID.EncapsulatedPDFStorage)
            {
                var pdfBytes = file.Dataset.GetValue<byte[]>(DicomTag.EncapsulatedDocument, 0);
                var pdfName = $"{studyUid}_{seriesUid}_{sopUid}.pdf";
                var pdfOut = Path.Combine("C:\\ecg-poc\\ingest", pdfName);
                Directory.CreateDirectory(Path.GetDirectoryName(pdfOut)!);
                File.WriteAllBytes(pdfOut, pdfBytes);
            }

            return Task.FromResult(new DicomCStoreResponse(req, DicomStatus.Success));
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "C-STORE failed");
            return Task.FromResult(new DicomCStoreResponse(req, DicomStatus.ProcessingFailure));
        }
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
        => Task.CompletedTask;
}
