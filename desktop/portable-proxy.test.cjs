'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),http=require('node:http'),{spawn}=require('node:child_process');
test('bundled Node fetch honors portable HTTP proxy even with the incompatible UI plugin suspended',async()=>{
 let requests=0;const sockets=new Set();
 const server=http.createServer((req,res)=>{requests++;res.end('ok');});
 server.on('connect',(req,socket)=>{
  requests++;sockets.add(socket);socket.on('close',()=>sockets.delete(socket));
  socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
  socket.once('data',()=>socket.end('HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok'));
 });
 await new Promise(r=>server.listen(0,'127.0.0.1',r));
 try{
  const proxy='http://127.0.0.1:'+server.address().port;
  const child=spawn(process.execPath,['--use-env-proxy','-e',"fetch('http://portable-proxy.invalid/check').then(r=>r.text()).then(v=>{if(v!=='ok')process.exitCode=1}).catch(()=>process.exitCode=2)"],{env:{...process.env,HTTP_PROXY:proxy,HTTPS_PROXY:proxy,NO_PROXY:'',http_proxy:proxy,https_proxy:proxy,no_proxy:''},stdio:'ignore',windowsHide:true});
  const code=await new Promise((resolve,reject)=>{const timer=setTimeout(()=>{child.kill();reject(Error('proxy test timeout'));},10000);child.once('error',e=>{clearTimeout(timer);reject(e);});child.once('close',c=>{clearTimeout(timer);resolve(c);});});
  assert.equal(code,0);assert.ok(requests>0);
 }finally{for(const socket of sockets)socket.destroy();await new Promise(r=>server.close(r));}
});
