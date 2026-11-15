/// `value` in decimal, written by the registry crate `itoa`.
pub fn decimal(value: u32) -> String {
    itoa::Buffer::new().format(value).to_owned()
}
