import { describe, expect, it, vi } from 'vitest';
import { fetchGreeting } from './api';

function answer(status: number, body: unknown) {
  return vi.fn().mockResolvedValue({ ok: status < 400, status, json: async () => body });
}

describe('fetchGreeting', () => {
  it('returns the message and encodes the name in the path', async () => {
    const fetchMock = answer(200, { message: 'Hello, Ada Lovelace!' });
    vi.stubGlobal('fetch', fetchMock);

    expect(await fetchGreeting(' Ada Lovelace ')).toEqual({ ok: true, message: 'Hello, Ada Lovelace!' });
    expect(fetchMock.mock.calls[0][0]).toBe('/api/greetings/Ada%20Lovelace');
  });

  it('returns the servers error for a name it refuses', async () => {
    vi.stubGlobal('fetch', answer(400, { error: 'The name must be 1 to 50 letters.' }));

    expect(await fetchGreeting('<b>')).toEqual({ ok: false, error: 'The name must be 1 to 50 letters.' });
  });

  it('says so when the server cannot be reached', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('network down')));

    expect(await fetchGreeting('Ada')).toEqual({ ok: false, error: 'The server could not be reached. Is the API running?' });
  });
});
