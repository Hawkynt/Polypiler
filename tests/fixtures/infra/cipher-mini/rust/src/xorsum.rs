use std::cell::{Cell, RefCell};
use std::rc::Rc;

pub trait Algorithm {
    fn field_name(&self) -> &RefCell<Vec<u16>>;

    fn create_instance(&self, _is_inverse: bool) -> Rc<dyn IAlgorithmInstance> {
        panic!("CreateInstance() not implemented")
    }
}

pub trait IAlgorithmInstance {
    fn field_input(&self) -> &RefCell<Vec<u8>>;

    fn field_key(&self) -> &Cell<i32>;

    fn feed(&self, data: &[u8]) {
        self.field_input().borrow_mut().extend_from_slice(data);
    }

    fn set_key(&self, key: i32) {
        self.field_key().set(key & 0xff);
    }

    fn explode(&self) {
        panic!("boom")
    }

    fn result(&self) -> Vec<u8>;
}

#[derive(Default)]
pub struct XorSum {
    name: RefCell<Vec<u16>>,
    seed: Cell<i32>,
}

impl XorSum {
    pub fn new(name: &str, seed: i32) -> Rc<Self> {
        let this = Rc::new(Self::default());
        *this.name.borrow_mut() = name.encode_utf16().collect();
        this.seed.set(seed);
        this
    }
}

impl Algorithm for XorSum {
    fn field_name(&self) -> &RefCell<Vec<u16>> {
        &self.name
    }

    fn create_instance(&self, _is_inverse: bool) -> Rc<dyn IAlgorithmInstance> {
        XorSumInstance::new(self)
    }
}

#[derive(Default)]
pub struct XorSumInstance {
    input: RefCell<Vec<u8>>,
    seed: Cell<i32>,
    key: Cell<i32>,
}

impl XorSumInstance {
    pub fn new(algorithm: &XorSum) -> Rc<Self> {
        let this = Rc::new(Self::default());
        this.seed.set(algorithm.seed.get());
        this
    }
}

impl IAlgorithmInstance for XorSumInstance {
    fn field_input(&self) -> &RefCell<Vec<u8>> {
        &self.input
    }

    fn field_key(&self) -> &Cell<i32> {
        &self.key
    }

    fn result(&self) -> Vec<u8> {
        let mut sum = self.seed.get() ^ self.key.get();
        for &byte in self.input.borrow().iter() {
            sum = (sum ^ i32::from(byte)) & 0xff;
        }
        vec![sum as u8]
    }
}

pub fn algorithms() -> Vec<Rc<dyn Algorithm>> {
    vec![XorSum::new("XorSum-0", 0), XorSum::new("XorSum-7", 7)]
}
