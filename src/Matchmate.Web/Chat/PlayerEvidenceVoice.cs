using System.Collections.Frozen;

namespace Matchmate.Web.Chat;

public sealed record PlayerEvidenceDisplayNote(string Text, string Qualification);

// Display copy only. Exact source pairs stay authoritative; changed or unknown pairs retain both originals.
public static class PlayerEvidenceVoice
{
    private static readonly FrozenDictionary<(string Text, string Qualification), PlayerEvidenceDisplayNote> Notes =
        new Dictionary<(string Text, string Qualification), PlayerEvidenceDisplayNote>
        {
            [("Both club reports attribute an early layoff to Welbeck that set up Neto's shot, saved by Kelleher, after a Chavarria delivery.",
                "Nine versus tenth-minute wording retained. One reported chance contribution, not a formal goal assist, exact contact time, chance total or complete performance rating. Still samples do not prove every contact.")] = new(
                "Both club reports describe Welbeck laying the ball off for Neto's shot, saved by Kelleher, after Chavarria's delivery.",
                "The reports place it in the ninth or tenth minute. This describes one chance, not a credited goal assist, a tally of chances or an overall performance verdict. The still images don't confirm every contact or its exact timing."),
            [("A readable Chelsea18 is inside the penalty area alongside a defender, with the ball at or immediately in front of him.",
                "Point identity/ball proximity only. The run/offside commentary remains unverified; no confirmed offside, successful carry, shot, speed or run distance is established. Repeated footage is not an extra action.")] = new(
                "In the reviewed image, Chelsea's No.18 is in the penalty area alongside a defender, with the ball at or just in front of him.",
                "That shows his position and the nearby ball at one instant. The suggested run and offside are unconfirmed; it doesn't prove a successful carry or shot, or show how fast or far he ran. A repeat of the footage isn't another action."),
            [("Readable Chelsea18 is centrally positioned in attack while a teammate has the ball farther left.",
                "Position/presence at one reviewed instant. No proven intended recipient, missed pass, shot, movement duration, tactical assignment or lost opportunity. A repeated view is not another action.")] = new(
                "In the reviewed image, Chelsea's No.18 is central in attack while a teammate has the ball farther left.",
                "This is one instant. It doesn't tell us who a pass was meant for, whether a pass was missed or a shot followed, how long anyone moved, what their tactical job was or whether an opportunity was lost. A repeated view isn't another action."),
            [("Chavarria delivered; Welbeck set up Neto's saved shot; Kelleher saved.",
                "One reported chance, not multiple incidents, a formal goal assist or a complete action total. Danny's existing exact observation remains unchanged.")] = new(
                "The report describes Chavarria's delivery, Welbeck setting up Neto's shot and Kelleher making the save.",
                "This is one reported chance, not several incidents, a credited goal assist or a complete count of actions."),
            [("Kelleher held Rogers's free-kick after Damsgaard conceded it.",
                "No inferred foul intent, card, formal defensive error or shot total.")] = new(
                "The report says Kelleher held Rogers's free-kick after Damsgaard conceded it.",
                "It doesn't tell us what Damsgaard intended or establish a booking or a defensive mistake. It isn't a count of all the shots."),
            [("Henderson's corner passed Kelleher; Chelsea did not convert.",
                "Chelsea says the keeper missed the delivery; this alone does not establish culpability, a goal or an exact minute.")] = new(
                "The report describes Henderson's corner passing Kelleher without Chelsea converting.",
                "Chelsea says Kelleher missed the delivery. That doesn't establish a goal, an exact minute or who was at fault."),
            [("Kayode's throw reached Lewis-Potter, whose volley Palmer blocked.",
                "Named roles are attributed to the report; exact minute/contact times remain unknown.")] = new(
                "According to the report, Kayode's throw found Lewis-Potter, whose volley was blocked by Palmer.",
                "Those named roles come from the report; the exact minute and contact times aren't known."),
            [("Chavarria supplied Rogers; Schuster blocked his shot.",
                "Delivery, attempt and block are reported; no exact minute or complete statistics.")] = new(
                "The report has Chavarria finding Rogers and Schuster blocking the shot.",
                "The delivery, shot and block come from the report; the exact minute and complete statistics aren't available."),
            [("Schuster contacted a corner; Colwill could not turn the loose ball goalward.",
                "Do not silently equate this contact with the differently described untimed Chelsea corner or infer a measured reaction speed.")] = new(
                "The report describes Schuster making contact with a corner and Colwill being unable to turn a loose ball towards goal.",
                "We can't be sure this is the same corner as Chelsea's differently worded, untimed account, and it doesn't tell us anyone's reaction speed."),
            [("Lewis-Potter fed Damsgaard, who shot over.",
                "A selected reported example; no total or formal assist.")] = new(
                "The report describes Lewis-Potter finding Damsgaard, who shot over.",
                "This is one reported example, not a complete total or a credited goal assist."),
            [("Janelt's shot was blocked; Thiago supplied Lewis-Potter, whose shot Martinez saved.",
                "The blocker is not named; do not assign that block to a roster player.")] = new(
                "The report describes Janelt's shot being blocked. It also describes Thiago finding Lewis-Potter, whose shot Martinez saved.",
                "The report doesn't name who blocked Janelt's shot."),
            [("Thiago held up Damsgaard's pass under Colwill's pressure; Martinez saved Yarmoliuk's shot.",
                "Pressure and the reported attacking sequence do not establish an assignment, measured duel or individual blame.")] = new(
                "The report describes Thiago holding up Damsgaard's pass under Colwill's pressure. It also describes Martinez saving Yarmoliuk's shot.",
                "That account doesn't establish anyone's defensive assignment or fault, and it gives no measurement of the contest."),
            [("Schade and Thiago linked to Yarmoliuk's cross; Anthony headed against the bar, offside.",
                "An attributed offside passage, not a goal or exact contact time. Chelsea separately reports a goalkeeper touch; preserve that source distinction.")] = new(
                "The report links Schade and Thiago to Yarmoliuk's cross. It also describes Anthony heading against the bar in a passage reported as offside.",
                "This is a reported offside passage, not a goal, and the exact contact time isn't known. Chelsea's account also mentions a goalkeeper touch; that extra detail comes from Chelsea."),
            [("Janelt combined with Damsgaard, whose cross narrowly evaded Anthony.",
                "No completed assist, missed assignment, player movement metric or invented timestamp.")] = new(
                "The report describes Janelt combining with Damsgaard, whose cross narrowly missed Anthony.",
                "It doesn't establish a completed assist or a missed defensive assignment, and gives no exact timing or measurements of player movement."),
            [("Martinez touched Anthony's header onto the bar, with offside flagged.",
                "Chelsea's goalkeeper-touch account remains reported; not independently established by Brentford's crossbar description or a generated replay.")] = new(
                "Chelsea reports Martinez touching Anthony's header onto the bar, with offside flagged.",
                "The touch is Chelsea's account. Brentford's description of the header hitting the bar doesn't independently confirm it, and the illustrated replay cannot verify the contact."),
            [("Fofana headed clear before Thiago's volley went over.",
                "Selected reported defensive/attacking contributions; no aerial-duel total or blame.")] = new(
                "The report says Fofana headed clear before Thiago's volley went over.",
                "These are reported examples of defending and attacking, not an aerial-duel total or evidence of individual blame."),
            [("Palmer turned Sangare; his deflected shot went behind.",
                "The report names Sangare as the nearby opponent, not the player who deflected the shot.")] = new(
                "The report says Palmer turned Sangare and his deflected shot went behind.",
                "Sangare is named as the nearby opponent; the report doesn't say who deflected the shot."),
            [("Chelsea attributes a counter-attack pass and wide effort to Martinez and Chavarria.",
                "Chelsea names Chavarria; Brentford names Neto in a similarly placed pre-goal effort. Whether these are the same attack is unresolved. Do not add them as two attempts or infer a measured pass length, intended role or verified actor mapping. ASR commentary remains a locator, not independent confirmation.")] = new(
                "Chelsea's report links Martinez and Chavarria to a counter-attack pass and a wide effort.",
                "Chelsea names Chavarria, while Brentford names Neto in a similarly placed attack before the goal. They may describe the same move, so they can't safely be counted as separate attempts. Who was involved isn't independently settled, and no intended role or measured pass length is established. The commentary transcript isn't independent confirmation."),
            [("Brentford attributes an untimed second-half effort to Neto.",
                "Brentford names Neto; Chelsea describes a similarly placed pre-goal counter through Chavarria. Whether these accounts concern the same attack is unresolved. Preserve attributed identity disagreement; do not count two attempts or treat either intermediate actor as independently verified.")] = new(
                "Brentford's report attributes a second-half effort to Neto without giving an exact time.",
                "Brentford names Neto; Chelsea describes a similarly placed counter before the goal involving Chavarria. It is unclear whether they mean the same attack, so this doesn't establish two attempts or independently settle who was involved."),
            [("Barco had a penalty appeal rejected amid blocked attempts.",
                "Reported appeal/outcome, not proof of a foul, referee error, VAR intervention or Barco's individual shot total.")] = new(
                "The report describes Barco's penalty appeal being rejected amid blocked attempts.",
                "A rejected appeal doesn't prove a foul, a refereeing mistake or a VAR intervention, and it doesn't give Barco's shot total."),
            [("Rogers shot from the rebound.",
                "Source describes an unsuccessful effort; no player total or exact minute supplied.")] = new(
                "The report describes Rogers shooting from the rebound.",
                "The effort was unsuccessful in the report; the exact minute and Rogers's total attempts aren't supplied."),
            [("Nicoll-Jazuli made his senior debut as a late substitute.",
                "Participation/context, not a performance rating, exact playing-time total or verified on-ball contribution.")] = new(
                "The report notes Nicoll-Jazuli's senior debut as a late substitute.",
                "That establishes participation, not how well he played, his exact playing time or a verified contribution on the ball."),
            [("Brentford11 faces a Chelsea player with the ball near the touchline.",
                "Readable shirt11 at this point only; no completed tackle, interception, recovery or outcome is established.")] = new(
                "In the reviewed image, Brentford's No.11 faces a Chelsea player, with the ball near the touchline.",
                "The shirt identifies him at that instant, but the image doesn't show a completed tackle, interception, ball recovery or what happened next."),
            [("Brentford25 is outside Chelsea penalty area alongside Chelsea41 during the attack.",
                "Readable shirt25 at this point only; no previous delivery, pass, run duration or defensive assignment is identified.")] = new(
                "In the reviewed image, Brentford's No.25 is outside Chelsea's penalty area alongside Chelsea's No.41 during the attack.",
                "This identifies a position at one instant, not an earlier delivery or pass, how long a run lasted or a defensive assignment."),
            [("Chelsea32 is inside his own penalty area during a Brentford attack.",
                "Readable shirt32 at this point only; no preceding contact, marking assignment or defensive outcome is established.")] = new(
                "In the reviewed image, Chelsea's No.32 is inside his own penalty area during a Brentford attack.",
                "The shirt identifies him at that instant; it doesn't establish an earlier contact, a marking assignment or how the defending ended."),
            [("Chelsea41 is outside his own penalty area alongside Brentford25.",
                "Readable shirt41 at this point only; this does not identify a late corner taker or any previous contact.")] = new(
                "In the reviewed image, Chelsea's No.41 is outside his own penalty area alongside Brentford's No.25.",
                "This identifies him at that instant, not who took a late corner or made an earlier contact."),
            [("The broadcast close-up reads G.QUENDA23.",
                "Identity and presence only. The camera cut does not identify Quenda as the player making the preceding wide delivery.")] = new(
                "The broadcast close-up shows G. Quenda, No.23.",
                "It identifies who is in that shot, but the camera cut doesn't link Quenda to the preceding wide delivery."),
            [("Readable BARCO4 is lying on the pitch.",
                "No injury, cause, foul, penalty, intent or bridge to the preceding contact is established.")] = new(
                "In the reviewed image, Barco's name and No.4 are readable as he lies on the pitch.",
                "The image doesn't tell us if he was injured, what caused him to be down or whether a foul or penalty occurred. It also doesn't establish intent or link him to the preceding contact."),
            [("Readable LACROIX5 appears in close-up beside a Brentford player.",
                "A point identity only. The preceding header, contact and its outcome are not established; the broadcast shows added time +5.")] = new(
                "The close-up shows the readable name Lacroix and No.5 beside a Brentford player.",
                "That identifies him at one instant, without establishing the preceding header, contact or outcome. The broadcast's +5 marker refers to added time, not the time of a contact."),
            [("Chelsea14 is outside the image-left side of Brentford penalty area while the ball is farther left.",
                "Readable shirt14 at this point only; no pass receipt, delivery or intended-recipient role is established.")] = new(
                "In the reviewed image, Chelsea's No.14 is outside the left-hand side of Brentford's penalty area as seen on screen, with the ball farther left.",
                "The shirt identifies him at that instant; it doesn't show him receiving a pass or making a delivery, or establish that a pass was intended for him."),
        }.ToFrozenDictionary();

    public static PlayerEvidenceDisplayNote Display(string text, string qualification)
        => Notes.TryGetValue((text, qualification), out var note) ? note : new(text, qualification);
}
