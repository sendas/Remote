const ws = new WebSocket('wss://localhost:8443/ws/agent'); ws.binaryType='arraybuffer';
ws.onopen = () => {
  ws.send(JSON.stringify({ type:'hello', key: process.env.KEY, id:'EBVGPCT1', hostname:'EBVGPCT1', room:'Sala TIC1', school:'EBVG', user:'ESCOLA\\aluno.pct1', os:'sim', version:'1' }));
  setTimeout(()=>ws.send(JSON.stringify({type:'hand'})),800);
  setTimeout(()=>ws.send(JSON.stringify({type:'chat',text:'Stôr, não percebi o exercício 3'})),1600);
};
ws.onmessage = e => { if (typeof e.data==='string'){ const m=JSON.parse(e.data); if(m.type==='chat') setTimeout(()=>ws.send(JSON.stringify({type:'chat',text:'Obrigado!'})),300);} };
setTimeout(()=>process.exit(0), 8000);
