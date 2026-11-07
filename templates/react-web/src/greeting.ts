export const MAX_NAME_LENGTH = 50;

/** A greeting for the name, or null if it is empty, too long, or has anything but letters, digits, spaces, dots and dashes. */
export function greetingFor(name: string): string | null {
  const trimmed = name.trim();
  if (trimmed.length === 0 || trimmed.length > MAX_NAME_LENGTH || !/^[\p{L}\p{N} .-]+$/u.test(trimmed)) {
    return null;
  }
  return `Hello, ${trimmed}!`;
}
