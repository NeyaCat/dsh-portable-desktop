(async () => {
 const report = value => chrome.webview.postMessage('desktop:plugin-verification:' + JSON.stringify(value));
 const call=async(method,args={})=>{const response=await fetch('/api/'+method,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({type:'client-request',rpcId:crypto.randomUUID(),method,payload:{args}})});const body=await response.json();if(!body.result?.ok)throw Error(JSON.stringify(body.result?.error??body));return body.result.value;};
 try {
  const installed=await(await fetch('/dsh-market/installed')).json();
  report({proxyRepair:true,activation:installed.activation?.['dsh-http-proxy'],mounted:!document.body.innerText.includes('Failed to load plugins')});
  const before=(await call('settings/describe')).namespaces.find(row=>row.ns==='http-proxy');
  if(!before)throw Error('Proxy configuration missing');
  const user=before.user??{};
  try {
   await call('settings/mutate',{ns:'http-proxy',ops:[{op:'set',path:['excludeHosts'],value:[...(before.value.excludeHosts??[]),'portable-proxy-verification.invalid']}],expectedRevision:before.revision});
   const after=(await call('settings/describe')).namespaces.find(row=>row.ns==='http-proxy');
   if(!after.value.excludeHosts.includes('portable-proxy-verification.invalid'))throw Error('Write not persisted');
   report({proxySettingsReadWrite:true});
  }finally{
   await call('settings/mutate',{ns:'http-proxy',ops:[Object.hasOwn(user,'excludeHosts')?{op:'set',path:['excludeHosts'],value:user.excludeHosts}:{op:'unset',path:['excludeHosts']}]});
   const restored=(await call('settings/describe')).namespaces.find(row=>row.ns==='http-proxy');
   report({proxySettingsRestored:JSON.stringify(restored.value)===JSON.stringify(before.value)});
  }
 }catch(error){report({proxyRepairError:String(error)});}
})();
