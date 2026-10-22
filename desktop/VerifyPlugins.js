(async () => {
  const report = x => chrome.webview.postMessage('desktop:plugin-verification:' + JSON.stringify(x));
  try {
    const installed = await (await fetch('/dsh-market/installed')).json();
    for (const name of ['@michengai/dsh-agency-agents','@nanmicoder/dsh-agent-teams','dsh-at-file'])
      report({name, activation: installed.activation?.[name]});
    const call = async (method, args = {}) => {
      const r = await fetch('/api/' + method, {method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({type:'client-request',rpcId:crypto.randomUUID(),method,payload:{args}})});
      const body = await r.json();
      if (!body.result?.ok) throw new Error(JSON.stringify(body));
      return body.result.value;
    };
    const before = await call('atFile/getSettings');
    report({settingsRead:true, keys:Object.keys(before)});
    try {
      await call('atFile/updateSettings',{update:{field:'ignorePastedMentions',value:!before.ignorePastedMentions}});
      const changed = await call('atFile/getSettings');
      if(changed.ignorePastedMentions === before.ignorePastedMentions) throw Error('Settings did not change');
      report({settingsWrite:true});
    } finally {
      await call('atFile/updateSettings',{update:{field:'ignorePastedMentions',value:before.ignorePastedMentions}});
    }
    report({settingsRestored:true});
    const sessions = await call('session/list', {_request:{}});
    if (sessions.items.length) {
      const entries = await call('atFile/search', {agentId:sessions.items[0].sessionId});
      if (!Array.isArray(entries)) throw Error('Search returned no array');
      report({fileSearch:true, entryCount:entries.length});
    } else report({fileSearch:'No existing session available'});
    if (window.__portableRestartTest) {
      report({requestingInternalRestart:true});
      await fetch('/dsh-market/restart',{method:'POST',headers:{'content-type':'application/json'},body:'{}'});
    }
  } catch(error) { report({error:String(error)}); }
})();
