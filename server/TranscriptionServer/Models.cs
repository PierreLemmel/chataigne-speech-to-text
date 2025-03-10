namespace TranscriptionServer;

public record ElaboratingSentence(Guid SentenceId, IReadOnlyCollection<ElaboratingSentenceElement> Elements, TimeSpan StartTime):
    SpeechSentence(SentenceId, StartTime);

public record ElaboratingSentenceElement(string Transcript, bool IsStable);
    

public record FinalizedSentence(Guid SentenceId, IReadOnlyCollection<Word> Words, float Confidence, TimeSpan StartTime, TimeSpan EndTime):
    SpeechSentence(SentenceId, StartTime);

public record Word(TimeSpan StartTime, TimeSpan EndTime, string Transcript);

public record SpeechSentence(Guid SentenceId, TimeSpan StartTime);

public record SpeechSession(DateTime SessionBeginning, DateTime SessionEnding, IReadOnlyCollection<FinalizedSentence> Sentences);