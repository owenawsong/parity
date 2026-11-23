const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const hook = 'M8.25 4.5h1.125a2.25 2.25 0 0 1 0 4.5H8.25v8.75a1.75 1.75 0 1 1-3.5 0V8a3.5 3.5 0 0 1 3.5-3.5Z';
$('.mark').innerHTML='<svg viewBox="0 0 24 24" aria-hidden="true" style="fill:currentColor;stroke:none"><path d="'+hook+'"/><path d="'+hook+'" transform="rotate(180 12 12)"/></svg>';
$('#popup').setAttribute('aria-label','Nearby AirPods');
const paths = {
  buds:'M5 3a4 4 0 0 1 4 4v10a2 2 0 0 1-4 0V9A3 3 0 0 1 5 3ZM19 3a4 4 0 0 0-4 4v10a2 2 0 0 0 4 0V9a3 3 0 0 0 0-6Z',
  sound:'M5 3v5m0 5v8m7-18v10m0 5v3m7-18v2m0 5v11M2 10h6m1 6h6m1-9h6',
  stem:'M7 6a4 4 0 1 1 6 3.5V18a2 2 0 0 1-4 0V10M17 10q3 2 0 5m2-7q6 4 0 9',
  near:'M7 17a7 7 0 1 1 10 0M9 14a4 4 0 1 1 6 0m-3-3v9',
  sun:'M12 2v2m0 16v2M2 12h2m16 0h2M5 5l2 2m10 10 2 2M5 19l2-2M17 7l2-2m-3 7a4 4 0 1 1-8 0 4 4 0 1 1 8 0',
  adaptive:'M2 12h3l2-4 3 10 4-14 3 12 2-4h3',
  transparency:'M3 9q4-5 8 0t10 0M3 15q4-5 8 0t10 0',
  cancel:'M3 5q9 6 18 0M3 19q9-6 18 0M4 12h16',
  conversation:'M3 4h15v11H9l-5 4v-4H3zM9 8h4',
  volume:'M3 9h4l5-4v14l-5-4H3zM16 8q4 4 0 8m3-11q7 7 0 14',
  moon:'M19 16A8 8 0 0 1 9 4a8 8 0 1 0 10 12Z',play:'m7 4 13 8-13 8z'
};
function icon(key){return '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="'+paths[key]+'"/></svg>'}
const values=[50,50,50], switches={conversation:false,volume:false,ear:true,sleep:false};
let custom=false, connected=true, sequence=0, playing=false, audio, filters, master, timer;
const reduce=matchMedia('(prefers-reduced-motion: reduce)');
function reading(n,label,charge=false){return '<div class="reading"><span class="ring" style="--pct:'+n+'%" data-charge="'+(charge?'ϟ':'')+'" aria-hidden="true"></span><strong>'+n+'%</strong><small>'+label+(charge?' · charging':'')+'</small></div>'}
$('#objects').innerHTML=[['left','Left',78],['right','Right',64],['case','Case',92]].map(([key,label,n])=>'<div class="piece"><img src="media/pro3-'+key+'.webp" alt="">'+reading(n,label)+'</div>').join('');
$('#popupBatt').innerHTML=reading(64,'AirPods')+reading(92,'Case');
function page(name){for(const el of $$('main section'))el.hidden=el.id!==name; for(const b of $$('[data-page]')){b.classList.toggle('selected',b.dataset.page===name); if(b.dataset.page===name)b.setAttribute('aria-current','page');else b.removeAttribute('aria-current')} if(name!=='Sound') stop();document.title=name+' · Pairside preview'}
for(const b of $$('[data-page],[data-go]'))b.onclick=()=>page(b.dataset.page||b.dataset.go);
const modes=[['Transparency','transparency'],['Adaptive','adaptive'],['Noise Cancellation','cancel']];
$('#modes').innerHTML=modes.map(([name,key])=>'<button class="mode" aria-pressed="'+(key==='adaptive')+'">'+icon(key)+name+'</button>').join('');
for(const b of $$('.mode'))b.onclick=()=>{for(const other of $$('.mode'))other.setAttribute('aria-pressed',other===b);$('#adaptive').hidden=b.textContent!=='Adaptive'};
$('#adapt').oninput=e=>e.target.setAttribute('aria-valuetext',['Less noise','Balanced','More noise'][e.target.value]);$('#adapt').dispatchEvent(new Event('input'));
function updateSwitches(){for(const b of $$('[data-switch]'))b.setAttribute('aria-checked',switches[b.dataset.switch])}
for(const b of $$('[data-switch]'))b.onclick=()=>{switches[b.dataset.switch]=!switches[b.dataset.switch];updateSwitches()};updateSwitches();
$('#stems').innerHTML=['Left','Right'].map(side=>'<label class="row"><i data-icon="stem"></i><span>'+side+' stem</span><select aria-label="'+side+' stem action"><option>Listening mode</option><option>Microphone mute</option></select></label>').join('');
for(const el of $$('[data-icon]'))el.innerHTML=icon(el.dataset.icon);
function eq(){ $('#defaultEQ').setAttribute('aria-pressed',!custom);$('#customEQ').setAttribute('aria-pressed',custom);$('#eqSummary').textContent=custom?'Custom':'Device default';$('#reset').disabled=!custom||values.every(n=>n===50);$$('.band').forEach((b,i)=>{const n=custom?values[i]-50:0;b.querySelector('output').textContent=n>0?'+'+n:String(n);b.querySelector('input').value=n+50;if(filters)filters[i].gain.setTargetAtTime(n*.15,audio.currentTime,.025)}) }
$('#bands').innerHTML=['Bass','Midrange','Treble'].map(name=>'<label class="band"><output></output><input type="range" min="0" max="100" value="50" aria-label="'+name+'"><span>'+name+'</span></label>').join('');
$$('.band input').forEach((input,i)=>input.oninput=()=>{custom=true;values[i]=Number(input.value);eq()});$('#customEQ').onclick=()=>{custom=true;eq()};$('#defaultEQ').onclick=()=>{custom=false;eq()};$('#reset').onclick=()=>{values.fill(50);eq()};eq();
let tick=0,session=0;
function note(){const t=audio.currentTime;for(const [freq,type,level,len] of [[[110,130.81,146.83,98][tick%4],'sine',.34,.42],[[440,523.25,587.33,392,659.25,587.33,523.25,493.88][tick%8],'triangle',.16,.28],[2300+tick%3*220,'triangle',.043,.09]]){const o=audio.createOscillator(),g=audio.createGain();o.type=type;o.frequency.value=freq;g.gain.setValueAtTime(.0001,t);g.gain.exponentialRampToValueAtTime(level,t+.016);g.gain.exponentialRampToValueAtTime(.0001,t+len);o.connect(g).connect(filters[0]);o.onended=()=>{o.disconnect();g.disconnect()};o.start(t);o.stop(t+len+.01)}tick++}
function stop(){session++;playing=false;clearInterval(timer);if(master)master.gain.setTargetAtTime(0,audio.currentTime,.02);$('#audition span').textContent='Play sound sample';$('#audition').setAttribute('aria-pressed',false)}
$('#audition').onclick=async()=>{if(playing){stop();return}const run=++session;playing=true;try{if(!audio){audio=new AudioContext();filters=['lowshelf','peaking','highshelf'].map((type,i)=>{const f=audio.createBiquadFilter();f.type=type;f.frequency.value=[240,1150,3200][i];return f});master=audio.createGain();master.gain.value=0;filters[0].connect(filters[1]).connect(filters[2]).connect(master).connect(audio.destination)}await audio.resume();if(run!==session)return;master.gain.setTargetAtTime(.135,audio.currentTime,.022);eq();tick=0;note();timer=setInterval(note,430);$('#audition span').textContent='Stop sample';$('#audition').setAttribute('aria-pressed',true)}catch{stop();$('#audition span').textContent='Retry sound sample'}};
const popup=$('#popup'), spin=$('#spin'), transit=$('#transition');
function state(on){connected=on;sequence++;transit.pause();transit.hidden=true;$('#objects').hidden=!on;$('#offline').hidden=on;$('#hardware').classList.toggle('off',!on);$('#link').disabled=false;$('#link').textContent=on?'Disconnect':'Connect';for(const el of $$('.mode,#adapt'))el.disabled=!on}
let closeTimer;
function closePopup(){if(popup.classList.contains('leaving'))return;popup.classList.add('leaving');closeTimer=setTimeout(()=>popup.close(),reduce.matches?0:150)}
popup.oncancel=e=>{e.preventDefault();closePopup()};popup.onclose=()=>{clearTimeout(closeTimer);popup.classList.remove('leaving');spin.pause()};$('#dismiss').onclick=closePopup;
$('#near').onclick=()=>{page('Overview');state(false);popup.showModal();spin.currentTime=0;if(!reduce.matches)spin.play().catch(()=>{})};
function join(){page('Overview');state(false);const id=sequence;$('#offline').hidden=true;$('#hardware').classList.remove('off');$('#link').textContent='Connecting…';$('#link').disabled=true;const done=()=>{if(sequence!==id)return;state(true);$('#objects').animate([{opacity:0},{opacity:1}],{duration:reduce.matches?0:210})};if(reduce.matches){setTimeout(done,300);return}transit.hidden=false;transit.src='media/lift-out-fade.webm';transit.playbackRate=1.5;transit.onended=done;transit.play().catch(done);setTimeout(done,5000)}
$('#connect').onclick=()=>{closePopup();join()};$('#link').onclick=()=>connected?state(false):join();
const light=JSON.parse(localStorage.getItem('pairside-demo-light')||'false');function theme(v){document.documentElement.classList.toggle('light',v);$('#theme span').textContent=v?'Dark appearance':'Light appearance';localStorage.setItem('pairside-demo-light',JSON.stringify(v))}theme(light);$('#theme').onclick=()=>theme(!document.documentElement.classList.contains('light'));document.addEventListener('visibilitychange',()=>{if(document.hidden){stop();spin.pause()}else if(popup.open&&!reduce.matches)spin.play().catch(()=>{})});window.addEventListener('pagehide',stop);state(true);page('Overview');
