// Guard against SDK versions that treat a non-412 failed write as modified:true.
exports.createSafeFetch = (transport = globalThis.fetch) => async (input, init) => {
  const response = await transport(input, init);
  const method = (init?.method || input?.method || 'GET').toUpperCase();
  const expected = (method === 'GET' && response.status === 404) || (method === 'PUT' && response.status === 412);
  if (!response.ok && !expected) throw new Error('Netlify storage HTTP ' + response.status);
  // The SDK first requests a signed URL using PUT without a body. That is not a write receipt.
  if (method === 'PUT' && init?.body !== undefined && response.ok && !response.headers.get('etag')) throw new Error('Netlify write returned no ETag');
  return response;
};
