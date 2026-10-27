"""Small deterministic synthetic season/map queries; not a general replay engine."""
from datetime import datetime, timezone


def stamp(value):
    result = datetime.fromisoformat(value.replace('Z', '+00:00'))
    if result.utcoffset() is None:
        raise ValueError('Timestamp requires an explicit offset')
    return result.astimezone(timezone.utc)


def revisions_at(records, known_at):
    """Select knowledge-visible revisions BEFORE filtering their valid intervals."""
    cutoff = stamp(known_at)
    selected = {}
    for record in records:
        if record['provenance'] != 'synthetic':
            raise ValueError('This review query supports synthetic release time only')
        released = stamp(record['synthetic_released_at'])
        if released <= cutoff:
            prior = selected.get(record['record_id'])
            if prior is None or released > stamp(prior['synthetic_released_at']):
                selected[record['record_id']] = record
    return selected


def active(record, at):
    when = stamp(at)
    return stamp(record['valid_from']) <= when and (record['valid_to'] is None or when < stamp(record['valid_to']))


def check_season_relations(records):
    """Validate connected shapes. Historical visibility is tested by queries separately."""
    errors = []
    by_id = {}
    for r in records:
        by_id.setdefault(r['record_id'], []).append(r)
    pairs = {}
    for r in records:
        p, kind = r['payload'], r['kind']
        if kind == 'competition_season' and p['end_date'] < p['start_date']:
            errors.append(f"{r['revision_id']}: season ends before it starts")
        if kind == 'season_membership':
            key = (p['competition_season_id'], p['club_id'])
            if key in pairs and pairs[key] != r['record_id']:
                errors.append(f"{r['revision_id']}: duplicate club-season membership identity")
            pairs[key] = r['record_id']
        if kind == 'fixture':
            for pitch in by_id[p['pitch_id']]:
                if pitch['payload']['stadium_id'] != p['stadium_id']:
                    errors.append(f"{r['revision_id']}: fixture pitch belongs to another stadium")
            seasons = by_id[p['competition_season_id']]
            # Season date bounds are inclusive UTC calendar dates in this contract.
            day = (p['actual_kickoff'] or p['scheduled_kickoff'])[:10]
            if not any(s['payload']['start_date'] <= day <= s['payload']['end_date'] for s in seasons):
                errors.append(f"{r['revision_id']}: kickoff outside season dates")
            cutoff = r['synthetic_released_at'] if r['provenance'] == 'synthetic' else r['recorded_at']
            memberships = {}
            for m in records:
                if m['kind'] != 'season_membership':
                    continue
                released = m['synthetic_released_at'] if m['provenance'] == 'synthetic' else m['recorded_at']
                prior = memberships.get(m['record_id'])
                if stamp(released) <= stamp(cutoff) and (prior is None or stamp(released) > prior[0]):
                    memberships[m['record_id']] = (stamp(released), m)
            for club in (p['home_club_id'], p['away_club_id']):
                if not any(m['payload']['competition_season_id'] == p['competition_season_id'] and m['payload']['club_id'] == club and m['payload']['status'] == 'participating' and active(m,p['actual_kickoff'] or p['scheduled_kickoff']) for _,m in memberships.values()):
                    errors.append(f"{r['revision_id']}: fixture club lacks participating membership")
    return errors


def season_view(bundle, season_id, at, known_at):
    """Validate input, then return season clubs/grounds and separately located fixtures.

    at selects a date within the season; known_at controls fictional knowledge.
    Home-ground markers use at, while fixture venue markers use fixture kickoff.
    """
    from validate_bundle import validate
    errors = validate(bundle)
    if errors:
        raise ValueError('; '.join(errors))
    known = revisions_at(bundle['records'], known_at)
    season = known.get(season_id)
    if season is None or season['kind'] != 'competition_season':
        raise ValueError('Season unavailable at knowledge cutoff')
    p = season['payload']
    if not active(season, at) or not p['start_date'] <= stamp(at).date().isoformat() <= p['end_date']:
        raise ValueError('Selected date outside season')
    visible = {key:r for key,r in known.items() if active(r, at)}
    def venue(sid, when):
        stadium = known.get(sid)
        if not stadium or stadium['kind'] != 'stadium' or not active(stadium, when):
            return None
        return {'stadium_id':sid,'revision_id':stadium['revision_id'],**stadium['payload']}
    clubs = []
    for membership in visible.values():
        m = membership['payload']
        if membership['kind'] != 'season_membership' or m['competition_season_id'] != season_id or m['status'] != 'participating':
            continue
        club = visible.get(m['club_id'])
        if not club:
            continue
        grounds=[]
        for use in visible.values():
            u=use['payload']
            if use['kind']=='club_stadium' and u['club_id']==club['record_id'] and u['use'] in ('home','temporary_home'):
                grounds.append({'use_revision_id':use['revision_id'],'use':u['use'],'venue':venue(u['stadium_id'],at)})
        clubs.append({'club_id':club['record_id'],'revision_id':club['revision_id'],'membership_revision_id':membership['revision_id'],**club['payload'],'grounds':grounds})
    fixtures=[]
    for r in visible.values():
        f=r['payload']
        if r['kind']=='fixture' and f['competition_season_id']==season_id:
            kickoff=f['actual_kickoff'] or f['scheduled_kickoff']
            fixtures.append({'fixture_id':r['record_id'],'revision_id':r['revision_id'],**f,'venue':venue(f['stadium_id'],kickoff)})
    return {'season_id':season_id,'season_revision_id':season['revision_id'],'at':at,'known_at':known_at,'membership_coverage':p['membership_coverage'],'clubs':sorted(clubs,key=lambda c:c['club_id']),'fixtures':sorted(fixtures,key=lambda f:f['fixture_id'])}


def player_at_fixture(bundle, person_id, fixture_id, known_at):
    """Resolve dated identity/affiliations, not match selection or lineup eligibility."""
    from validate_bundle import validate
    errors=validate(bundle)
    if errors:
        raise ValueError('; '.join(errors))
    known=revisions_at(bundle['records'],known_at)
    fixture=known.get(fixture_id)
    if not fixture or fixture['kind']!='fixture':
        raise ValueError('Fixture unavailable at knowledge cutoff')
    when=fixture['payload']['actual_kickoff'] or fixture['payload']['scheduled_kickoff']
    person=known.get(person_id)
    if not person or person['kind']!='person' or not active(person,when):
        raise ValueError('Person unavailable at match date')
    born=person['payload']['birth_date']
    day=stamp(when).date()
    birth=datetime.fromisoformat(born).date() if born else None
    age=day.year-birth.year-((day.month,day.day)<(birth.month,birth.day)) if birth else None
    affiliations=[r for r in known.values() if r['kind']=='affiliation' and r['payload']['person_id']==person_id and active(r,when)]
    return {'person_id':person_id,'person_revision_id':person['revision_id'],'fixture_id':fixture_id,'at':when,'known_at':known_at,'age':age,'affiliations':affiliations}
