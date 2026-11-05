import api from '../config/api';

export const getCashReconciliationReport = (params) => {
  return api.get('/reports/cash-reconciliation', { params });
};

export const acknowledgeCashRegister = (id, acknowledged) => {
  return api.patch(`/reports/cash-register/${id}/acknowledge`, { acknowledged });
};
