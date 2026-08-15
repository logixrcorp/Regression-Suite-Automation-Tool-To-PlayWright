// Print a current TOTP code for an authenticator secret, so a seed can be
// checked without reaching for a phone.
//
//   npm run generate:otp -- <secret>
//
// With no argument it falls back to D365_OTP_SECRET from the environment.
import * as OTPAuth from 'otpauth';

const secret = process.argv[2] || process.env.D365_OTP_SECRET;

if (!secret) {
  console.error('Usage: npm run generate:otp -- <secret>   (or set D365_OTP_SECRET)');
  process.exit(1);
}

const totp = new OTPAuth.TOTP({
  issuer: 'Microsoft',
  algorithm: 'SHA1',
  digits: 6,
  period: 30,
  secret,
});

console.log(`OTP Code: ${totp.generate()}`);
