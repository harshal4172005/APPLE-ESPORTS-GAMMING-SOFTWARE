import api from '../config/api';

// --- Branches ---
export const getBranches = async () => {
  const response = await api.get('/branches');
  return response.data;
};

export const createBranch = async (branchData) => {
  const response = await api.post('/branches', branchData);
  return response.data;
};

export const updateBranch = async (id, branchData) => {
  const response = await api.put(`/branches/${id}`, branchData);
  return response.data;
};

export const deleteBranch = async (id) => {
  const response = await api.delete(`/branches/${id}`);
  return response.data;
};

export const deleteBranchPermanent = async (id) => {
  const response = await api.delete(`/branches/${id}/permanent`);
  return response.data;
};

// --- Food Groups (shared food/snacks menu + stock across linked branches) ---
export const getFoodGroups = async () => {
  const response = await api.get('/food-groups');
  return response.data;
};

export const createFoodGroup = async (name) => {
  const response = await api.post('/food-groups', { name });
  return response.data;
};

export const setFoodGroupBranches = async (id, branchIds) => {
  const response = await api.put(`/food-groups/${id}/branches`, branchIds);
  return response.data;
};

// The one branch that keeps selling the shared fridge when the internet is down.
export const setFoodGroupOfflineSeller = async (id, branchId) => {
  const response = await api.put(`/food-groups/${id}/offline-seller`, { branchId: branchId || null });
  return response.data;
};

export const deleteFoodGroup = async (id) => {
  const response = await api.delete(`/food-groups/${id}`);
  return response.data;
};

// --- Operators ---
export const getOperators = async () => {
  const response = await api.get('/operators');
  return response.data;
};

export const createOperator = async (operatorData) => {
  const response = await api.post('/operators', operatorData);
  return response.data;
};

export const updateOperator = async (id, operatorData) => {
  const response = await api.put(`/operators/${id}`, operatorData);
  return response.data;
};

export const deleteOperator = async (id) => {
  const response = await api.delete(`/operators/${id}`);
  return response.data;
};

export const manageOperatorAdminRole = async (id, data) => {
  const response = await api.post(`/operators/${id}/admin-role`, data);
  return response.data;
};

// --- PCs Management ---
export const getBranchPcs = async (branchId) => {
  const response = await api.get('/pcs', { params: { branchId } });
  return response.data;
};

// Full PC details for Settings page fleet management (includes specs, zone, hardwareNotes, etc.)
export const getBranchPcsDetailed = async (branchId) => {
  const response = await api.get('/pcs/details', { params: { branchId } });
  return response.data;
};

export const createPc = async (pcData) => {
  const response = await api.post('/pcs', pcData);
  return response.data;
};

export const updatePc = async (id, pcData) => {
  const response = await api.put(`/pcs/${id}`, pcData);
  return response.data;
};

export const deletePc = async (id) => {
  const response = await api.delete(`/pcs/${id}`);
  return response.data;
};

// --- Audit Logs ---
export const getAuditLogs = async () => {
  const response = await api.get('/audit-logs?pageSize=500');
  return response.data;
};

// --- Activation / Permanent Deletion ---
// Super Admin's per-branch switch: charge operators for stock their shift counts find missing.
export const setChargeMissingStock = async (id, enabled) => {
  const response = await api.put(`/branches/${id}/charge-missing-stock`, { enabled });
  return response.data;
};

export const activateBranch = async (id) => {
  const response = await api.post(`/branches/${id}/activate`);
  return response.data;
};

export const activateOperator = async (id) => {
  const response = await api.post(`/operators/${id}/activate`);
  return response.data;
};

export const deleteOperatorPermanent = async (id) => {
  const response = await api.delete(`/operators/${id}/permanent`);
  return response.data;
};

// --- System Configuration ---
export const getSystemConfigs = async () => {
  const response = await api.get('/system-config');
  return response.data;
};

export const saveSystemConfig = async (data) => {
  const response = await api.post('/system-config', data);
  return response.data;
};

export const testEmailConfig = async (toAddress) => {
  const response = await api.post('/system-config/test-email', { toAddress });
  return response.data;
};

// --- Wallet Top-Up Settings ---
export const getWalletTopUpRules = async () => {
  const response = await api.get('/wallet-settings');
  return response.data;
};

export const saveWalletTopUpRules = async (data) => {
  const response = await api.put('/wallet-settings', data);
  return response.data;
};

// --- Pricing Profiles ---
export const getPricingProfiles = async (branchId) => {
  const response = await api.get(`/pricing-profiles?branchId=${branchId}`);
  return response.data;
};

export const createPricingProfile = async (data) => {
  const response = await api.post('/pricing-profiles', data);
  return response.data;
};

export const updatePricingProfile = async (id, data) => {
  const response = await api.put(`/pricing-profiles/${id}`, data);
  return response.data;
};

export const deletePricingProfile = async (id) => {
  const response = await api.delete(`/pricing-profiles/${id}`);
  return response.data;
};

export const createPricingPackage = async (data) => {
  const response = await api.post('/pricing-profiles/packages', data);
  return response.data;
};

export const updatePricingPackage = async (id, data) => {
  const response = await api.put(`/pricing-profiles/packages/${id}`, data);
  return response.data;
};

export const deletePricingPackage = async (id) => {
  const response = await api.delete(`/pricing-profiles/packages/${id}`);
  return response.data;
};

export const forceLogoutOperator = async (id) => {
  const response = await api.post(`/auth/force-logout/${id}`);
  return response.data;
};
