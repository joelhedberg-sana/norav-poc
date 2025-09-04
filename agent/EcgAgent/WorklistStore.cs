using FellowOakDicom;

public static class WorklistStore
{
    // Simple list; replace with DB later. Each item is already a fully populated MWL dataset.
    private static readonly List<DicomDataset> _items = new();

    public static void Add(DicomDataset ds)
    {
        lock (_items) _items.Add(ds);
    }

    // naive filter: match on PatientID / Accession
    public static IEnumerable<DicomDataset> Query(DicomDataset keys)
    {
        var pid = keys.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
        var acc = keys.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty);

        lock (_items)
        {
            return _items.Where(i =>
                (string.IsNullOrEmpty(pid) || i.GetString(DicomTag.PatientID) == pid) &&
                (string.IsNullOrEmpty(acc) || i.GetString(DicomTag.AccessionNumber) == acc)
            ).ToList();
        }
    }

    // Convert your app's demographics to a proper MWL item dataset
    public static DicomDataset FromOrder(string patientId, string name, string? accession)
    {
        var now = DateTime.UtcNow;
        return new DicomDataset
        {
            { DicomTag.SpecificCharacterSet, "ISO_IR 100" },
            { DicomTag.PatientID, patientId },
            { DicomTag.PatientName, name },
            { DicomTag.AccessionNumber, accession ?? string.Empty },
            { DicomTag.Modality, "ECG" }, // not standardized modality token, but acceptable for MWL
            { DicomTag.ScheduledProcedureStepSequence, new DicomSequence(
                DicomTag.ScheduledProcedureStepSequence, // FIX: supply tag for sequence ctor
                new DicomDataset {
                    { DicomTag.ScheduledStationAETitle, "NEKO_ECG" },
                    { DicomTag.ScheduledProcedureStepStartDate, now.ToString("yyyyMMdd") },
                    { DicomTag.ScheduledProcedureStepStartTime, now.ToString("HHmmss") },
                    { DicomTag.ScheduledPerformingPhysicianName, "Auto" },
                    { DicomTag.ScheduledProcedureStepDescription, "Resting ECG" }
                })
            }
        };
    }
}
