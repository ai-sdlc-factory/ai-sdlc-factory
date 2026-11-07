import { describe, expect, it } from 'vitest';
import { greetingFor, MAX_NAME_LENGTH } from './greeting';

describe('greetingFor', () => {
  it('greets a usable name', () => {
    expect(greetingFor('  Ada Lovelace ')).toBe('Hello, Ada Lovelace!');
  });

  it.each(['', '   ', '<script>', 'name; drop table'])('refuses %j', (name) => {
    expect(greetingFor(name)).toBeNull();
  });

  it('refuses a name over the limit and accepts one at the limit', () => {
    expect(greetingFor('a'.repeat(MAX_NAME_LENGTH + 1))).toBeNull();
    expect(greetingFor('a'.repeat(MAX_NAME_LENGTH))).not.toBeNull();
  });
});
