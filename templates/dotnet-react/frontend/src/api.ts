/** The result of asking the API for a greeting: a message, or the reason there is none. */
export type GreetingResult = { ok: true; message: string } | { ok: false; error: string };

export async function fetchGreeting(name: string, signal?: AbortSignal): Promise<GreetingResult> {
  try {
    const response = await fetch(`/api/greetings/${encodeURIComponent(name.trim())}`, { signal });
    const body = (await response.json()) as { message?: string; error?: string };
    if (response.ok && typeof body.message === 'string') {
      return { ok: true, message: body.message };
    }
    return { ok: false, error: body.error ?? `The server answered ${response.status}.` };
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error;
    }
    return { ok: false, error: 'The server could not be reached. Is the API running?' };
  }
}
