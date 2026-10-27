namespace Matchmate.Web.Localization;

public sealed record SourceNotePresentation(string OriginalText, string? OriginalUncertainty, string Text,
    string? Uncertainty, string LanguageCode, bool IsTranslated, bool IsFallback);
public sealed record SourceNoteQualification(string Original, string Text, string LanguageCode, bool IsFallback, int[] Steps);

/// <summary>Reviewed display translations only. Source records and model evidence remain untouched.</summary>
public static class SourceNoteText
{
    private const string LiverpoolGoal = "Named action is reported; geometry and offsets are generated. Pixels do not independently identify each contact; exact source seconds are unknown.";
    private const string LiverpoolGoalSv = "Den namngivna handlingen återges i en rapport; geometrin och tidsförskjutningarna är genererade. Källbilderna räcker inte för att oberoende bekräfta varje bollkontakt; exakta tidpunkter i källan är okända.";
    private const string LiverpoolGoalEs = "La acción del jugador identificado procede de un informe; la geometría y los desfases temporales son generados. Las imágenes no identifican de forma independiente cada contacto con el balón; se desconocen los segundos exactos en la fuente.";
    private const string LiverpoolCard = "Reported card only; no tackle or referee location is reconstructed. Exact source seconds are unknown.";
    private const string LiverpoolCardSv = "Endast det rapporterade kortet återges; ingen tackling eller domarposition rekonstrueras. Exakta tidpunkter i källan är okända.";
    private const string LiverpoolCardEs = "Solo se recoge la tarjeta indicada en el informe; no se reconstruyen la entrada ni la posición del árbitro. Se desconocen los segundos exactos en la fuente.";
    private const string Booking = "No tackle, referee position or exact booking second is reconstructed.";
    private const string BookingSv = "Ingen tackling, domarposition eller exakt sekund för varningen rekonstrueras.";
    private const string BookingEs = "No se reconstruyen la entrada, la posición del árbitro ni el segundo exacto de la amonestación.";

    private sealed record Reviewed(string Kind, string English, string? Qualification, string Swedish, string? SwedishQualification,
        string Spanish, string? SpanishQualification);

    private static readonly IReadOnlyDictionary<(string Kind, string Text, string? Uncertainty), Reviewed> Notes = Build();

    public static SourceNotePresentation Get(string kind, string text, string? uncertainty, MatchLanguage language)
    {
        language = MatchLanguages.Normalize(language);
        if (language == MatchLanguage.English)
            return new(text, uncertainty, text, uncertainty, "en", false, false);
        if (!Notes.TryGetValue((kind, text, uncertainty), out var note))
            return new(text, uncertainty, text, uncertainty, "en", false, true);
        return language == MatchLanguage.Swedish
            ? new(text, uncertainty, note.Swedish, note.SwedishQualification, "sv", true, false)
            : new(text, uncertainty, note.Spanish, note.SpanishQualification, "es", true, false);
    }

    // Group only exact original qualifications with the same reviewed display result. An unknown
    // tuple must retain its English qualification even when another step has a reviewed translation.
    public static SourceNoteQualification[] Qualifications(IEnumerable<SourceNotePresentation> notes) => notes
        .Select((note, index) => (Note: note, Step: index + 1))
        .Where(row => !string.IsNullOrEmpty(row.Note.OriginalUncertainty))
        .GroupBy(row => (Original: row.Note.OriginalUncertainty!, Text: row.Note.Uncertainty!, row.Note.LanguageCode, row.Note.IsFallback))
        .Select(group => new SourceNoteQualification(group.Key.Original, group.Key.Text, group.Key.LanguageCode,
            group.Key.IsFallback, group.Select(row => row.Step).ToArray())).ToArray();

    private static IReadOnlyDictionary<(string, string, string?), Reviewed> Build()
    {
        Reviewed[] reviewed =
        [
            new("corner", "Reported Damsgaard corner kick.", "Reported sequence; actual delivery time and trajectory are unknown.",
                "Enligt rapporten slår Damsgaard hörnan.", "Sekvensen återges enligt rapporten; den faktiska tidpunkten och bollbanan är okända.",
                "Según el informe, Damsgaard saca el córner.", "La secuencia procede del informe; se desconocen el momento real del envío y la trayectoria."),
            new("headed_assist", "Reported Schuster header across goal.", "Wide review does not independently identify the intermediate actor or contact.",
                "Enligt rapporten nickar Schuster tvärs över målet.", "Granskningen av översiktsbilderna identifierar inte oberoende spelaren eller bollkontakten i mellanledet.",
                "Según el informe, Schuster prolonga de cabeza el balón frente a la portería.", "La revisión de los planos generales no identifica de forma independiente al jugador ni el contacto intermedio."),
            new("header_finish", "Reported Anthony header from very close range.", "Schematic contacts and positions illustrate the report, not measured movement.",
                "Enligt rapporten nickar Anthony från mycket nära håll.", "Schematiska bollkontakter och positioner illustrerar rapporten och bygger inte på uppmätta rörelser.",
                "Según el informe, Anthony cabecea desde muy cerca.", "Los contactos y las posiciones del esquema ilustran el informe y no representan movimientos medidos."),
            new("release_pass", "Brentford report: Damsgaard releases Schade.", "Release passer is attributed to Brentford; timing and geometry remain illustrative.",
                "Brentfords rapport: Damsgaard spelar fram Schade.", "Uppgiften om passningsläggaren kommer från Brentford; tidpunkter och geometri är fortfarande illustrativa.",
                "Informe del Brentford: Damsgaard habilita a Schade.", "La identificación del pasador se atribuye al Brentford; los tiempos y la geometría siguen siendo ilustrativos."),
            new("shot", "Both club reports: Schade shoots.", "Native identity points do not independently establish a continuous named path or shot contact.",
                "Båda klubbarnas rapporter: Schade skjuter.", "Punktvisa identitetsbelägg i originalbilderna fastställer inte oberoende en sammanhängande rörelse för den namngivna spelaren eller kontakten vid skottet.",
                "Informes de ambos clubes: Schade dispara.", "Los puntos de identificación en las imágenes originales no establecen de forma independiente una trayectoria continua del jugador identificado ni el contacto del disparo."),
            new("save", "Both club reports: Martinez saves Schade's attempt.", "Reported save; sampled wide footage does not independently verify this contact.",
                "Båda klubbarnas rapporter: Martinez räddar Schades försök.", "Räddningen återges enligt rapporterna; de granskade översiktsbilderna verifierar inte oberoende denna bollkontakt.",
                "Informes de ambos clubes: Martinez detiene el intento de Schade.", "La parada procede de los informes; los planos generales muestreados no verifican de forma independiente este contacto."),
            new("rebound_finish", "Both club reports: Thiago scores the rebound.", "Formal assist remains uncredited. Selected report minute83 differs from retained Brentford minute80.",
                "Båda klubbarnas rapporter: Thiago gör mål på returen.", "Ingen formell assist är registrerad. Den valda rapportens minut 83 skiljer sig från minut 80 i den bevarade Brentfordrapporten.",
                "Informes de ambos clubes: Thiago marca en el rechace.", "No consta una asistencia oficial. El minuto 83 del informe seleccionado difiere del minuto 80 del informe del Brentford conservado."),
            new("throw_in", "Native point and exact-fixture roster: Schade at throw-in setup.", "Point-scoped identity; exact release contact and authored timing are not visually established.",
                "En punkt i originalbilderna och spelarlistan för just denna match: Schade vid förberedelsen av inkastet.", "Identiteten gäller bara denna punkt; den exakta utkastkontakten och de författade tidpunkterna är inte visuellt fastställda.",
                "Punto en las imágenes originales y plantilla de este partido concreto: Schade en la preparación del saque de banda.", "La identificación se limita a ese punto; las imágenes no establecen el contacto exacto del saque ni los tiempos fijados para la ilustración."),
            new("into_area", "Separate native point and roster: Schade is the later ball carrier.", "Intermediate throw recipient/return passer is unresolved; these separate points do not establish a continuous named path.",
                "En separat punkt i originalbilderna och spelarlistan: Schade är den senare bollföraren.", "Mottagaren av inkastet eller spelaren som passar tillbaka är inte fastställd; dessa separata punkter visar inte en sammanhängande rörelse för en namngiven spelare.",
                "Otro punto en las imágenes originales y la plantilla: Schade conduce el balón más adelante.", "No se ha identificado al receptor intermedio del saque ni al autor del pase de vuelta; estos puntos separados no establecen una trayectoria continua de un jugador identificado."),
            new("cross", "Both club reports: Schade crosses.", "Reported cross, not independently verified pixel contact.",
                "Båda klubbarnas rapporter: Schade slår inlägget.", "Inlägget återges enligt rapporterna; bollkontakten har inte verifierats oberoende i bilderna.",
                "Informes de ambos clubes: Schade centra.", "El centro procede de los informes; el contacto no se ha verificado de forma independiente en las imágenes."),
            new("deflection", "Brentford report alone: slight Acheampong deflection.", "Single-source report attribution; sampled native frames do not independently verify the actor/contact.",
                "Enbart Brentfords rapport: en lätt styrning via Acheampong.", "Uppgiften kommer från en enda rapportkälla; de granskade originalbilderna verifierar inte oberoende spelaren eller bollkontakten.",
                "Solo el informe del Brentford: ligero desvío de Acheampong.", "La atribución procede de un único informe; los fotogramas originales muestreados no verifican de forma independiente al jugador ni el contacto."),
            new("reported_finish", "Both club reports: Carvalho finishes at the far post.", "Formal assist remains uncredited; exact contacts and a continuous named path are not established.",
                "Båda klubbarnas rapporter: Carvalho avslutar vid bortre stolpen.", "Ingen formell assist är registrerad; exakta bollkontakter och en sammanhängande rörelse för en namngiven spelare är inte fastställda.",
                "Informes de ambos clubes: Carvalho remata en el segundo palo.", "No consta una asistencia oficial; no se han establecido los contactos exactos ni una trayectoria continua de un jugador identificado."),
            new("substitution", "Reported outgoing and incoming player transition.", "Same-minute substitutions share a release; exact within-group order and running paths are unknown.",
                "Rapporterat byte med utgående och inkommande spelare.", "Byten i samma minut visas samtidigt; den exakta ordningen inom gruppen och löpvägarna är okända.",
                "Cambio de jugador saliente y entrante según el informe.", "Los cambios del mismo minuto se muestran a la vez; se desconocen el orden exacto dentro del grupo y las trayectorias de carrera."),
            new("booking", "Reported booking: excessive celebration.", Booking,
                "Rapporterad varning: överdrivet firande.", BookingSv, "Amonestación según el informe: celebración excesiva.", BookingEs),
            new("booking", "Reported booking: bad foul.", Booking,
                "Rapporterad varning: ful förseelse.", BookingSv, "Amonestación según el informe: falta dura.", BookingEs),
            new("release_pass", "Club report: Pass to Thiago.", LiverpoolGoal,
                "Klubbrapport: passning till Thiago.", LiverpoolGoalSv, "Informe del club: pase a Thiago.", LiverpoolGoalEs),
            new("cross", "Club report: Cross into the centre.", LiverpoolGoal,
                "Klubbrapport: inlägg mot mitten.", LiverpoolGoalSv, "Informe del club: centro al medio.", LiverpoolGoalEs),
            new("header_finish", "Club report: Header past Alisson.", LiverpoolGoal,
                "Klubbrapport: nick förbi Alisson.", LiverpoolGoalSv, "Informe del club: cabezazo que supera a Alisson.", LiverpoolGoalEs),
            new("release_pass", "Club report: Pass into the right channel.", LiverpoolGoal,
                "Klubbrapport: passning ut i höger kanal.", LiverpoolGoalSv, "Informe del club: pase al carril derecho.", LiverpoolGoalEs),
            new("advance", "Club report: Check inside.", LiverpoolGoal,
                "Klubbrapport: vändning inåt.", LiverpoolGoalSv, "Informe del club: recorte hacia dentro.", LiverpoolGoalEs),
            new("shot", "Club report: Left-footed finish into the bottom corner.", LiverpoolGoal,
                "Klubbrapport: avslut med vänsterfoten i nedre hörnet.", LiverpoolGoalSv, "Informe del club: remate con la izquierda a la esquina inferior.", LiverpoolGoalEs),
            new("cross", "Club report: Cross towards the back post.", LiverpoolGoal,
                "Klubbrapport: inlägg mot bortre stolpen.", LiverpoolGoalSv, "Informe del club: centro hacia el segundo palo.", LiverpoolGoalEs),
            new("headed_assist", "Club report: Headed knock-down.", LiverpoolGoal,
                "Klubbrapport: nednickning.", LiverpoolGoalSv, "Informe del club: dejada de cabeza.", LiverpoolGoalEs),
            new("shot", "Club report: Control and finish from close range.", LiverpoolGoal,
                "Klubbrapport: mottagning och avslut från nära håll.", LiverpoolGoalSv, "Informe del club: control y remate desde cerca.", LiverpoolGoalEs),
            new("booking", "Reported yellow card: bad foul.", LiverpoolCard,
                "Rapporterat gult kort: ful förseelse.", LiverpoolCardSv, "Tarjeta amarilla según el informe: falta dura.", LiverpoolCardEs),
            new("booking", "Reported yellow card; reason not supplied.", LiverpoolCard,
                "Rapporterat gult kort; orsaken anges inte.", LiverpoolCardSv, "Tarjeta amarilla según el informe; no se indica el motivo.", LiverpoolCardEs),
            new("red_card", "Reported straight-red dismissal: violent conduct.", LiverpoolCard,
                "Rapporterad utvisning med direkt rött kort: våldsamt uppträdande.", LiverpoolCardSv, "Expulsión con roja directa según el informe: conducta violenta.", LiverpoolCardEs),
            new("red_card", "Reported second-yellow dismissal: bad foul.", LiverpoolCard,
                "Rapporterad utvisning efter andra gula kortet: ful förseelse.", LiverpoolCardSv, "Expulsión por segunda amarilla según el informe: falta dura.", LiverpoolCardEs),
            new("reported_assist", "Officially credited assist; delivery type unknown.", null,
                "Officiellt registrerad assist; typen av framspelning är okänd.", null, "Asistencia acreditada oficialmente; se desconoce el tipo de envío.", null),
            new("reported_finish", "Officially reported goal; actual contact unknown.", null,
                "Officiellt rapporterat mål; den faktiska bollkontakten är okänd.", null, "Gol comunicado oficialmente; se desconoce el contacto real.", null),
            new("advance", "Reviewed attacker advances towards the area.", "Build-up actor identity is unverified.",
                "Den granskade anfallaren avancerar mot straffområdet.", "Spelarens identitet i uppspelet är inte verifierad.",
                "El atacante observado avanza hacia el área.", "No se ha verificado la identidad del jugador que participa en la elaboración."),
            new("keeper_out", "Reviewed goalkeeper comes out.", "Goalkeeper identity and exact positions are unverified.",
                "Den granskade målvakten kommer ut.", "Målvaktens identitet och exakta positioner är inte verifierade.",
                "El portero observado sale de su portería.", "No se han verificado la identidad del portero ni sus posiciones exactas."),
            new("uncertain_goalmouth", "Reviewed action crosses the goalmouth.", "Pass versus parry is unknown; no assist identity is inferred.",
                "Den granskade situationen går tvärs över målområdet.", "Det är okänt om det är en passning eller en räddningsretur; ingen assistläggare härleds.",
                "La acción observada cruza la boca de gol.", "Se desconoce si fue un pase o un despeje del portero; no se deduce la identidad de un asistente."),
            new("reported_finish", "Reported Thiago right-foot finish from the centre of the box following a fast break.", "Review does not identify the finisher; report minute 83 differs from retained club report minute 80.",
                "Enligt rapporten avslutar Thiago med högerfoten från mitten av straffområdet efter en snabb kontring.", "Granskningen identifierar inte avslutaren; rapportens minut 83 skiljer sig från minut 80 i den bevarade klubbrapporten.",
                "Según el informe, Thiago remata con la derecha desde el centro del área tras un contraataque rápido.", "La revisión no identifica al rematador; el minuto 83 del informe difiere del minuto 80 del informe del club conservado."),
            new("throw_in", "Reviewed throw-in.", "Thrower identity and exact contact timing are unverified.",
                "Granskat inkast.", "Inkastarens identitet och den exakta tidpunkten för bollkontakten är inte verifierade.",
                "Saque de banda observado.", "No se han verificado la identidad del lanzador ni el momento exacto del contacto."),
            new("into_area", "Reviewed subsequent action enters the area.", "Build-up actors and exact contacts are unknown.",
                "Den granskade fortsättningen går in i straffområdet.", "Spelarna i uppspelet och de exakta bollkontakterna är okända.",
                "La continuación observada entra en el área.", "Se desconocen los jugadores que participan en la elaboración y los contactos exactos."),
            new("reported_finish", "Reported Carvalho right-foot finish from the right side of the six-yard box.", "Scorer is not readable in reviewed samples; no explicit assist credit.",
                "Enligt rapporten avslutar Carvalho med högerfoten från högra delen av målområdet.", "Målskytten kan inte identifieras i de granskade bilderna; ingen uttrycklig assist är registrerad.",
                "Según el informe, Carvalho remata con la derecha desde el lado derecho del área pequeña.", "No se puede identificar al goleador en las muestras revisadas; no hay una asistencia acreditada explícitamente."),
            new("completed_possession", "Completed source-annotated possession interval; not possession now.", null,
                "Avslutat bollinnehavsintervall enligt källans anteckningar; visar inte bollinnehavet nu.", null,
                "Intervalo de posesión finalizado y anotado en la fuente; no indica la posesión actual.", null),
            new("completed_possession", "Completed source-annotated possession interval; not possession now.", "One or both annotated interval endpoints are not matched to a tracking frame.",
                "Avslutat bollinnehavsintervall enligt källans anteckningar; visar inte bollinnehavet nu.", "En eller båda angivna intervallgränserna saknar matchning mot en spårningsbildruta.",
                "Intervalo de posesión finalizado y anotado en la fuente; no indica la posesión actual.", "Uno o ambos extremos anotados del intervalo no coinciden con un fotograma de seguimiento.")
        ];
        return reviewed.ToDictionary(note => (note.Kind, note.English, note.Qualification));
    }
}
