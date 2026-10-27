(function (root) {
  'use strict';
  // Drawing positions are illustration units, never latitude/longitude.
  const rows = [
    ['bournemouth','AFC Bournemouth','Bournemouth','Vitality Stadium / Dean Court','BOU',411,683],
    ['arsenal','Arsenal','London','Emirates Stadium','ARS',630,398],
    ['aston-villa','Aston Villa','Birmingham','Villa Park','AVL',400,557,'2015/16'],
    ['burnley','Burnley','Burnley','Turf Moor','BUR',387,430,'2016/17'],
    ['chelsea','Chelsea','London','Stamford Bridge','CHE',600,480],
    ['crystal-palace','Crystal Palace','London','Selhurst Park','CRY',649,535],
    ['everton','Everton','Liverpool','Goodison Park','EVE',308,458],
    ['hull','Hull City','Hull','KC Stadium','HUL',485,451,'2016/17'],
    ['leicester','Leicester City','Leicester','King Power Stadium','LEI',443,555],
    ['liverpool','Liverpool','Liverpool','Anfield','LIV',327,497],
    ['manchester-city','Manchester City','Manchester','Etihad Stadium','MCI',410,476],
    ['manchester-united','Manchester United','Manchester','Old Trafford','MUN',366,479],
    ['middlesbrough','Middlesbrough','Middlesbrough','Riverside Stadium','MID',458,394,'2016/17'],
    ['newcastle','Newcastle United','Newcastle upon Tyne',"St James’ Park",'NEW',447,316,'2015/16'],
    ['norwich','Norwich City','Norwich','Carrow Road','NOR',525,563,'2015/16'],
    ['southampton','Southampton','Southampton',"St Mary’s Stadium",'SOU',452,657],
    ['stoke','Stoke City','Stoke-on-Trent','Britannia Stadium','STK',394,514],
    ['sunderland','Sunderland','Sunderland','Stadium of Light','SUN',475,350],
    ['swansea','Swansea City','Swansea, Wales','Liberty Stadium','SWA',288,600],
    ['tottenham','Tottenham Hotspur','London','White Hart Lane','TOT',682,382],
    ['watford','Watford','Watford','Vicarage Road','WAT',578,367],
    ['west-brom','West Bromwich Albion','West Bromwich','The Hawthorns','WBA',338,545],
    ['west-ham','West Ham United','London','Boleyn Ground','WHU',700,464]
  ];
  const clubs = rows.map(([id,name,town,ground,initials,x,y,only]) => ({id,name,town,ground,initials,x,y,only,inset:['arsenal','chelsea','crystal-palace','tottenham','watford','west-ham'].includes(id)}));
  const seasons = ['2015/16','2016/17'];
  function filterSeasons(query = '') {
    const term = query.trim().replace(/\s/g,'').replace(/[-–]/g,'/');
    return seasons.filter(season => {
      const [start,end] = season.split('/');
      return season.startsWith(term) || `${start}/${start.slice(0,2)}${end}`.startsWith(term);
    });
  }
  // Only source-checked period variants are mapped. Never borrow today's
  // badge for a historical season whose variant has not been verified.
  const crestPaths = {
    arsenal:'assets/crests/arsenal.svg', chelsea:'assets/crests/chelsea.svg',
    leicester:'assets/crests/leicester.svg', liverpool:'assets/crests/liverpool.svg',
    tottenham:'assets/crests/tottenham.svg', newcastle:'assets/crests/newcastle.png',
    'manchester-city':'assets/crests/manchester-city-2016.svg',
    'west-ham':'assets/crests/west-ham-2016.png',
    'bournemouth':'assets/crests/bournemouth.png',
    'aston-villa':'assets/crests/aston-villa.png',
    'burnley':'assets/crests/burnley.png',
    'crystal-palace':'assets/crests/crystal-palace.png',
    'everton':'assets/crests/everton.png',
    'hull':'assets/crests/hull.png',
    'manchester-united':'assets/crests/manchester-united.png',
    'middlesbrough':'assets/crests/middlesbrough.png',
    'norwich':'assets/crests/norwich.png',
    'southampton':'assets/crests/southampton.png',
    'stoke':'assets/crests/stoke.png',
    'sunderland':'assets/crests/sunderland.png',
    'swansea':'assets/crests/swansea.png',
    'watford':'assets/crests/watford.png',
    'west-brom':'assets/crests/west-brom.png'
  };
  function crestFor(id, season, failed = false) {
    if (failed || !members(season).some(c => c.id === id)) return null;
    if (season === '2015/16' && id === 'manchester-city') return 'assets/crests/manchester-city-2015.png';
    if (season === '2015/16' && id === 'west-ham') return 'assets/crests/west-ham-2015.png';
    return crestPaths[id] || null;
  }
  function members(season) {
    if (!seasons.includes(season)) throw new Error('Unknown season');
    return clubs.filter(c => !c.only || c.only === season).map(c => ({...c,ground:c.id === 'west-ham' && season === '2016/17' ? 'London Stadium' : c.id === 'stoke' && season === '2016/17' ? 'bet365 Stadium' : c.ground}));
  }
  function transition(state, action) {
    if (action.type === 'view' && ['map','list'].includes(action.value)) return {...state,view:action.value};
    if (action.type === 'season') return {...state,season:action.value,selected:members(action.value).some(c => c.id === state.selected) ? state.selected : null};
    if (action.type === 'select' && members(state.season).some(c => c.id === action.value)) return {...state,selected:action.value};
    return state;
  }
  const model = {members,transition,crestFor,filterSeasons};
  if (typeof module !== 'undefined' && module.exports) module.exports = model;
  else root.LeagueModel = model;
})(typeof window !== 'undefined' ? window : globalThis);
